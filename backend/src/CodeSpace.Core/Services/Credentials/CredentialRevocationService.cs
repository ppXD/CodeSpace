using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.OAuth;
using CodeSpace.Messages.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CodeSpace.Core.Services.Credentials;

public sealed class CredentialRevocationService : ICredentialRevocationService, IScopedDependency
{
    /// <summary>
    /// What a repository left on a disconnected credential says, here and in every run that reads it: only the remedies a
    /// person can perform today — a team Admin re-linking the repository to an active credential of the same provider; the
    /// owner reconnecting, for a personal connection, which carries the repository to the new connection; and the way out when
    /// neither is wanted. Re-linking everything on the credential at once is an API (<c>RelinkCredentialBindingsCommand</c>)
    /// with no screen yet, so it is not offered here: a message that names a step nobody can find sends them hunting.
    /// </summary>
    public const string DisconnectedRepositoryError = "Credential disconnected. Re-link this repository to an active credential of the same provider (a team Admin can), or unbind it; if it was a personal connection, its owner reconnecting the provider moves it too.";

    private const string SweepName = "departed-credential-retirement";

    private readonly CodeSpaceDbContext _db;
    private readonly ISweepFailureBackoff _backoff;
    private readonly ICrossProcessTryLock _tryLock;
    private readonly ILogger<CredentialRevocationService> _logger;

    public CredentialRevocationService(CodeSpaceDbContext db, ISweepFailureBackoff backoff, ICrossProcessTryLock tryLock, ILogger<CredentialRevocationService> logger)
    {
        _db = db;
        _backoff = backoff;
        _tryLock = tryLock;
        _logger = logger;
    }

    public async Task<int> CascadeAsync(Guid credentialId, CancellationToken cancellationToken)
    {
        var affectedRepoCount = await MarkDependentRepositoriesUnauthorisedAsync(credentialId, cancellationToken).ConfigureAwait(false);

        await SoftDeleteDependentIdentitiesAsync(credentialId, cancellationToken).ConfigureAwait(false);

        return affectedRepoCount;
    }

    public async Task<int> RetireAsync(Credential credential, CancellationToken cancellationToken)
    {
        MarkRetired(credential);

        return await CascadeAsync(credential.Id, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The sweep's own rule, narrowed to one person in one team, so a departure and the sweep can never disagree on what
    /// goes. Every Personal credential the person lent the team goes, the already-disconnected ones included: the repair
    /// sweep moves a disconnected credential's bindings to its owner's newest live one, so a single live token left behind
    /// would hand the team back everything bound through the others. With none left, and no way to connect a new one in
    /// the name of someone without standing in the team, nothing bound through them follows anywhere. A team-service
    /// credential is the team's and survives membership changes — it is written owner-less, and is never retired here.
    /// </summary>
    public async Task RetireDepartedInTeamAsync(Guid teamId, Guid userId, CancellationToken cancellationToken)
    {
        var departed = await Departed()
            .Where(c => c.TeamId == teamId && (c.OwnerUserId ?? c.CreatedBy) == userId)
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        foreach (var credential in departed) await RetireAsync(credential, cancellationToken).ConfigureAwait(false);
    }

    public async Task<int> RetireDepartedAsync(int batchSize, CancellationToken cancellationToken) =>
        await new SweepPass<DepartedCredential>(SweepName, _backoff, _logger).RunAsync(batchSize, LoadDepartedAsync, RetireDepartedOneAsync, cancellationToken).ConfigureAwait(false);

    /// <summary>The next page of departed credentials in id order, read behind a cursor so a pass walks the whole backlog instead of re-reading the front of it, which is where a credential that can never be retired stays.</summary>
    private async Task<List<DepartedCredential>> LoadDepartedAsync(Guid? after, int take, CancellationToken cancellationToken)
    {
        var departed = Departed().AsNoTracking();

        if (after is Guid cursor) departed = departed.Where(c => c.Id.CompareTo(cursor) > 0);

        var ids = await departed.OrderBy(c => c.Id).Select(c => c.Id).Take(take).ToListAsync(cancellationToken).ConfigureAwait(false);

        return ids.Select(id => new DepartedCredential(id)).ToList();
    }

    /// <summary>
    /// Retire one candidate under the credential's refresh lock. A token somebody is writing — a refresh, or a re-authorization
    /// by a person who has just rejoined — is skipped, not waited for, and met again on the next tick: retiring it under that write
    /// would blank the token the write lands, and the person would be told their re-authorization worked.
    /// </summary>
    private async Task<bool> RetireDepartedOneAsync(DepartedCredential candidate, CancellationToken cancellationToken)
    {
        await using var held = await CredentialRefreshLock.TryAcquireAsync(_tryLock, candidate.Id, cancellationToken).ConfigureAwait(false);

        if (held is null) return SkipBusy(candidate);

        return await RetireHeldAsync(candidate, cancellationToken).ConfigureAwait(false);
    }

    private bool SkipBusy(DepartedCredential candidate)
    {
        _logger.LogInformation("{Sweep}: credential {CredentialId} is being written (a refresh or a re-authorization holds its lock); it stays a candidate for the next pass", SweepName, candidate.Id);

        return false;
    }

    /// <summary>
    /// Re-read under the candidate rule — now under the lock, so no write of the token can land between the verdict and the
    /// retirement — and retired on its own save, so one failure undoes no other. A person who rejoined since the candidate list
    /// was read keeps the token, and so does a re-authorization they completed before the lock was taken. The verdict is the
    /// person's standing and the credential's ownership, which no write of its health changes, so the lock is the whole exclusion here.
    /// </summary>
    private async Task<bool> RetireHeldAsync(DepartedCredential candidate, CancellationToken cancellationToken)
    {
        try
        {
            var credential = await Departed().SingleOrDefaultAsync(c => c.Id == candidate.Id, cancellationToken).ConfigureAwait(false);

            if (credential == null) return false;

            var brokenRepositoryCount = await RetireAsync(credential, cancellationToken).ConfigureAwait(false);

            await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

            _logger.LogInformation("Retired credential {CredentialId} in team {TeamId}: its person is no longer a member; {RepositoryCount} repositories bound through it now need a new credential", credential.Id, credential.TeamId, brokenRepositoryCount);

            return true;
        }
        finally
        {
            _db.ChangeTracker.Clear();
        }
    }

    /// <summary>The credentials the sweep retires — <see cref="DepartedCredentials.Of"/>.</summary>
    private IQueryable<Credential> Departed() => DepartedCredentials.Of(_db);

    /// <summary>
    /// Clear the token material — nothing in CodeSpace can present it once it is gone — and take the row out of the
    /// team's credential list. A row retired earlier keeps the date it was first retired on.
    /// </summary>
    private static void MarkRetired(Credential credential)
    {
        credential.EncryptedPayload = string.Empty;
        credential.Status = CredentialStatus.Revoked;
        credential.LastValidatedDate = null;
        credential.DeletedDate ??= DateTimeOffset.UtcNow;
    }

    /// <summary>
    /// Repositories bound through this credential lose their auth source the moment it's
    /// revoked. We DON'T cascade-delete them (too destructive — disconnect is often part of
    /// token rotation, and the user expects to come back), but we DO flip status to
    /// <see cref="RepositoryStatus.Error"/> with a concrete remediation message so the UI
    /// can show "Needs new credential" instead of pretending the repo is still healthy.
    ///
    /// Webhook ingestion is deliberately untouched — webhooks authenticate via the secret we
    /// registered, not via the OAuth token. Events keep flowing in, and once the user
    /// re-links to a new credential the repo is fully healthy again with no event gap.
    /// </summary>
    private async Task<int> MarkDependentRepositoriesUnauthorisedAsync(Guid credentialId, CancellationToken cancellationToken)
    {
        var candidates = await _db.Repository
            .Where(r => r.CredentialId == credentialId && r.DeletedDate == null && r.Status != RepositoryStatus.Error)
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        // Filtered again on the tracked rows, not only in SQL: a Disconnect that moved this credential's repositories in the
        // same unit of work has re-pointed them in memory while the database still names the dead credential, and they
        // are no longer this credential's to break.
        var affected = candidates.Where(r => r.CredentialId == credentialId).ToList();

        foreach (var repo in affected)
        {
            repo.Status = RepositoryStatus.Error;
            repo.LastError = DisconnectedRepositoryError;
        }

        return affected.Count;
    }

    /// <summary>
    /// A <see cref="UserProviderIdentity"/> (Model B act-as-user link) is unusable without its backing
    /// credential, so revoking the credential — e.g. Disconnect on the Personal tab — must soft-delete the
    /// linked identity too. Otherwise the Connected Identities surface keeps showing a dead link, out of
    /// sync with the Personal tab. Tracked + saved by the same unit of work as the revoke, and idempotent
    /// (no live identity ⇒ no-op) so the always-run cascade is safe on an already-revoked credential.
    /// </summary>
    private async Task SoftDeleteDependentIdentitiesAsync(Guid credentialId, CancellationToken cancellationToken)
    {
        var identities = await _db.UserProviderIdentity
            .Where(i => i.CredentialId == credentialId && i.DeletedDate == null)
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        var now = DateTimeOffset.UtcNow;
        foreach (var identity in identities) identity.DeletedDate = now;
    }

    private sealed record DepartedCredential(Guid Id) : ISweepCandidate;
}
