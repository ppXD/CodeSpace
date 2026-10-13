using System.Text.Json;
using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Persistence.Db;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CodeSpace.Core.Services.Workflows.Artifacts.Profiles;

/// <summary>
/// The bounded pass behind <see cref="IStorageNamespaceFingerprintBackfillService"/>. A candidate is a revision whose
/// stored fingerprint is not <see cref="StorageProfileRules.LegacyNamespaceFingerprint"/> of its own stored
/// configuration. Every build that predates physical namespaces wrote exactly that value, so the only revisions that
/// differ are the ones a physical-identity build wrote for a configuration not already in canonical form; after the
/// rewrite they leave the set, so the pass is self-terminating.
///
/// <para>Revisions are immutable under a trigger, so the pass suspends it inside its own transaction and restores it
/// before commit: no other session ever sees the table without it, and a pass that fails rolls the switch back with
/// its writes. The switch locks out concurrent admissions for the length of one short pass, and the lock wait is
/// bounded so a pass queued behind a long transaction gives up rather than stall them. Each row is rewritten by an
/// UPDATE conditional on its id AND the value the pass read, so a second worker restoring the same row matches
/// nothing.</para>
/// </summary>
public sealed class StorageNamespaceFingerprintBackfillService : IStorageNamespaceFingerprintBackfillService, IScopedDependency
{
    private const string BoundLockWait = "SET LOCAL lock_timeout = '5s'";
    private const string SuspendImmutability = "ALTER TABLE storage_profile_revision DISABLE TRIGGER storage_profile_revision_enforce_immutability";
    private const string ResumeImmutability = "ALTER TABLE storage_profile_revision ENABLE TRIGGER storage_profile_revision_enforce_immutability";

    private readonly CodeSpaceDbContext _db;
    private readonly ILogger<StorageNamespaceFingerprintBackfillService> _logger;

    public StorageNamespaceFingerprintBackfillService(CodeSpaceDbContext db, ILogger<StorageNamespaceFingerprintBackfillService> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task<int> BackfillAsync(int batchSize, CancellationToken cancellationToken)
    {
        var candidates = await LoadCandidatesAsync(batchSize, cancellationToken).ConfigureAwait(false);

        if (candidates.Count == 0) return 0;

        var restored = await RestoreAsync(candidates, cancellationToken).ConfigureAwait(false);

        _logger.LogInformation("Restored the stored namespace fingerprint of {Restored} storage profile revision(s) to the value the previous build compares", restored);

        return restored;
    }

    /// <summary>The oldest revisions whose stored fingerprint is not the one their configuration hashes to as written. The configuration table is small, so it is read whole and judged in memory by the writer's own rule.</summary>
    private async Task<IReadOnlyList<Candidate>> LoadCandidatesAsync(int batchSize, CancellationToken cancellationToken)
    {
        var revisions = await _db.StorageProfileRevision.AsNoTracking()
            .OrderBy(revision => revision.CreatedDate).ThenBy(revision => revision.Id)
            .Select(revision => new { revision.Id, revision.ProviderTypeKey, revision.NonSecretConfigJson, revision.NamespaceFingerprint })
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        return revisions
            .Select(revision => new Candidate(revision.Id, revision.NamespaceFingerprint, Legacy(revision.ProviderTypeKey, revision.NonSecretConfigJson)))
            .Where(candidate => candidate.Stored != candidate.Legacy)
            .Take(batchSize)
            .ToList();
    }

    private async Task<int> RestoreAsync(IReadOnlyList<Candidate> candidates, CancellationToken cancellationToken)
    {
        await using var transaction = await _db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        await _db.Database.ExecuteSqlRawAsync(BoundLockWait, cancellationToken).ConfigureAwait(false);
        await _db.Database.ExecuteSqlRawAsync(SuspendImmutability, cancellationToken).ConfigureAwait(false);

        var restored = 0;

        foreach (var candidate in candidates) restored += await RestoreOneAsync(candidate, cancellationToken).ConfigureAwait(false);

        await _db.Database.ExecuteSqlRawAsync(ResumeImmutability, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        return restored;
    }

    /// <summary>Rewrites the fingerprint alone, and only while the row still holds the value this pass read. 0 when another worker restored it first.</summary>
    private Task<int> RestoreOneAsync(Candidate candidate, CancellationToken cancellationToken) =>
        _db.StorageProfileRevision
            .Where(revision => revision.Id == candidate.Id && revision.NamespaceFingerprint == candidate.Stored)
            .ExecuteUpdateAsync(set => set.SetProperty(revision => revision.NamespaceFingerprint, candidate.Legacy), cancellationToken);

    private static string Legacy(string providerTypeKey, string nonSecretConfigJson)
    {
        using var configuration = JsonDocument.Parse(nonSecretConfigJson);
        return StorageProfileRules.LegacyNamespaceFingerprint(providerTypeKey, configuration.RootElement);
    }

    private sealed record Candidate(Guid Id, string Stored, string Legacy);
}
