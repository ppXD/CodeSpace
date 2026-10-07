using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Persistence.Db;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CodeSpace.Core.Services.Agents;

/// <summary>
/// The bounded seal pass behind <see cref="IPackCloneUrlBackfillService"/>. A candidate is a row whose URL carries
/// userinfo, judged by the writer's own rule (<see cref="PackCloneUrlProtector.CarriesCredential"/>) in memory BEFORE
/// the batch is cut, so a row with an '@' only in its path never crowds out a real one. A sealed row's URL no longer
/// carries userinfo, so it leaves the set: the pass is self-terminating.
///
/// <para>Each row is written by one UPDATE conditional on its id AND the URL the pass read, so a second worker sealing
/// the same row matches nothing and the ciphertext is written once. When sealing makes an active row's URL equal to
/// another active pack's (a legacy fork of one repository), the row becomes that pack's duplicate; when two rows of one
/// group race to hold it, the unique index refuses one, and the next pass finds the holder. No lock is needed.</para>
///
/// <para>Per-row try/catch: a failing row stays a candidate and never aborts the pass. The log names the pack and the
/// team, never the URL.</para>
/// </summary>
public sealed class PackCloneUrlBackfillService : IPackCloneUrlBackfillService, IScopedDependency
{
    private readonly CodeSpaceDbContext _db;
    private readonly IPackCloneUrlProtector _protector;
    private readonly ILogger<PackCloneUrlBackfillService> _logger;

    public PackCloneUrlBackfillService(CodeSpaceDbContext db, IPackCloneUrlProtector protector, ILogger<PackCloneUrlBackfillService> logger)
    {
        _db = db;
        _protector = protector;
        _logger = logger;
    }

    public async Task<int> BackfillAsync(int batchSize, CancellationToken cancellationToken)
    {
        var candidates = await LoadCandidatesAsync(batchSize, cancellationToken).ConfigureAwait(false);

        var sealedCount = 0;

        foreach (var candidate in candidates)
        {
            try
            {
                if (await SealAsync(candidate, cancellationToken).ConfigureAwait(false)) sealedCount++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning("Pack clone-URL backfill failed for pack {PackId} in team {TeamId} ({ExceptionType}); the pass continues — the pack stays a candidate", candidate.Id, candidate.TeamId, ex.GetType().Name);
            }
        }

        return sealedCount;
    }

    /// <summary>The oldest rows, active or soft-deleted, whose URL carries a credential. The '@' filter is a sound superset (userinfo needs one) that keeps the read small; the real rule runs in memory before the batch is cut.</summary>
    private async Task<IReadOnlyList<Candidate>> LoadCandidatesAsync(int batchSize, CancellationToken cancellationToken)
    {
        var rows = await _db.Pack.AsNoTracking()
            .Where(p => p.Url != null && p.Url.Contains("@"))
            .OrderBy(p => p.CreatedDate).ThenBy(p => p.Id)
            .Select(p => new Candidate(p.Id, p.TeamId, p.Url!, p.Subpath, p.DeletedDate == null))
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        return rows.Where(r => PackCloneUrlProtector.CarriesCredential(r.Url)).Take(batchSize).ToList();
    }

    /// <summary>Rewrite one row to its credential-free URL plus its sealed original, marking it a duplicate when another active pack already holds that URL. False when another worker sealed it first.</summary>
    private async Task<bool> SealAsync(Candidate candidate, CancellationToken cancellationToken)
    {
        var source = _protector.Seal(candidate.Url);

        var holderId = candidate.IsActive ? await FindHolderAsync(candidate, source.Url, cancellationToken).ConfigureAwait(false) : null;

        var written = await _db.Pack
            .Where(p => p.Id == candidate.Id && p.Url == candidate.Url)
            .ExecuteUpdateAsync(set => set.SetProperty(p => p.Url, source.Url).SetProperty(p => p.EncryptedCloneUrl, source.EncryptedCloneUrl).SetProperty(p => p.DuplicateOfPackId, holderId), cancellationToken).ConfigureAwait(false);

        return written == 1;
    }

    /// <summary>The active pack that already holds <paramref name="cleanUrl"/> as its source identity in the candidate's team and subpath — the one a sealed duplicate points at.</summary>
    private async Task<Guid?> FindHolderAsync(Candidate candidate, string cleanUrl, CancellationToken cancellationToken) =>
        await _db.Pack.AsNoTracking()
            .Where(p => p.TeamId == candidate.TeamId && p.Url == cleanUrl && (p.Subpath ?? "") == (candidate.Subpath ?? "") && p.DuplicateOfPackId == null && p.DeletedDate == null && p.Id != candidate.Id)
            .OrderBy(p => p.CreatedDate).ThenBy(p => p.Id)
            .Select(p => (Guid?)p.Id)
            .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);

    private sealed record Candidate(Guid Id, Guid TeamId, string Url, string? Subpath, bool IsActive);
}
