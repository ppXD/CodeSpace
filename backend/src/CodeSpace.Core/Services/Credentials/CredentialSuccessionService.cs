using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Middlewares.Transactional;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Webhooks.Registration;
using CodeSpace.Messages.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CodeSpace.Core.Services.Credentials;

/// <summary>
/// One rule, applied at connect time and by the recurring repair: what is bound through a DEAD credential — its
/// repositories and the group hooks it registered — follows that credential's owner to the credential they connected
/// after it on the same provider instance in the same team. Dead is a revoked credential, and one its provider refused for
/// good (<c>Error</c>, with its payload kept): the failure for either says to reconnect, and a person who does has no
/// Disconnect to click first — the Providers list shows only live credentials, so a refused one is already gone from it.
/// Ownership is the whole key — a teammate connecting the same
/// provider is a different identity, and moving the owner's bindings onto it would change who they act as. A live
/// credential has no successor, whatever its owner connects beside it — its bindings move only when it is disconnected
/// (<see cref="MoveBindingsAsync"/>, with the destination a person named, or — when the owner disconnects their own — the
/// one other connection <see cref="FindOnlyOtherConnectionAsync"/> implies) or a person relinks them
/// (<see cref="ICredentialRelinkService"/>), each a deliberate act rather than a side effect of connecting. The repair sweep
/// stays the fallback for what none of those doors reached, and walks its backlog in bounded pages (<see cref="SweepPass{TCandidate}"/>).
/// </summary>
public sealed partial class CredentialSuccessionService : ICredentialSuccessionService, IStrandedCredentialCensus, IScopedDependency
{
    private const string SweepName = "credential-binding-repair";

    /// <summary>The hook states a reconnect revives, as a bind revives them: the ones the dead credential's failures put there.</summary>
    private static readonly RepositoryWebhookRegistrationStatus[] Revivable = { RepositoryWebhookRegistrationStatus.Failed, RepositoryWebhookRegistrationStatus.DeadLettered };

    private readonly CodeSpaceDbContext _db;
    private readonly IConnectionWebhookRegistrationDispatcher _hookDispatcher;
    private readonly IPostCommitActions _postCommit;
    private readonly ISweepFailureBackoff _backoff;
    private readonly ILogger<CredentialSuccessionService> _logger;

    public CredentialSuccessionService(CodeSpaceDbContext db, IConnectionWebhookRegistrationDispatcher hookDispatcher, IPostCommitActions postCommit, ISweepFailureBackoff backoff, ILogger<CredentialSuccessionService> logger)
    {
        _db = db;
        _hookDispatcher = hookDispatcher;
        _postCommit = postCommit;
        _backoff = backoff;
        _logger = logger;
    }

    public async Task CarryForwardAsync(Credential successor, CancellationToken cancellationToken)
    {
        if (successor.OwnerUserId is not Guid ownerId) return;

        var predecessorIds = await LoadDeadPredecessorIdsAsync(successor, ownerId, cancellationToken).ConfigureAwait(false);

        if (predecessorIds.Count == 0) return;

        var adopted = await AdoptBindingsAsync(successor, predecessorIds, cancellationToken).ConfigureAwait(false);

        await DispatchAfterCommitAsync(adopted.RevivedHookIds, cancellationToken).ConfigureAwait(false);
    }

    public async Task<int> RepairStrandedAsync(int batchSize, CancellationToken cancellationToken) =>
        await new SweepPass<StrandedCredential>(SweepName, _backoff, _logger).RunAsync(batchSize, LoadStrandedAsync, RepairAsync, cancellationToken).ConfigureAwait(false);

    public async Task<int> CountAsync(CancellationToken cancellationToken) => await StrandedWithSuccessor().CountAsync(cancellationToken).ConfigureAwait(false);

    /// <summary>
    /// The owner's dead credentials on the successor's instance and team — revoked, or refused by their provider. Filtered on
    /// the tracked entities, not in SQL: a token link revokes the credential it replaces in the same unit of work, so that row
    /// is Revoked in memory before it is Revoked in the database. No date is asked of a refused one here: the successor is
    /// being created now, after whatever the owner's credentials last did.
    /// </summary>
    private async Task<List<Guid>> LoadDeadPredecessorIdsAsync(Credential successor, Guid ownerId, CancellationToken cancellationToken)
    {
        var owned = await _db.Credential
            .Where(c => c.TeamId == successor.TeamId && c.ProviderInstanceId == successor.ProviderInstanceId && c.OwnerUserId == ownerId && c.Id != successor.Id)
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        return owned.Where(c => c.Status is CredentialStatus.Revoked or CredentialStatus.Error).Select(c => c.Id).ToList();
    }

    /// <summary>
    /// Dead owned credentials that something live is still bound to, each paired with its successor: the owner's
    /// newest Active credential on the same instance and team connected at or after the moment the dead one stopped working. A revoked row is not modified again, so its last-modified date is when it was revoked (a token link revokes
    /// the credential it replaces in the same save that creates the new one, hence at-or-after). A refused row IS written again,
    /// every hour its verdict is confirmed, so its last-modified date says nothing about when it died: it was last known good
    /// at its last validation, or at its creation if it never had one, and a credential connected since is the owner's
    /// reconnect — while one connected before it last worked was beside it, not after it. A credential with no successor — a
    /// disconnect nobody reconnected — is not a candidate, so it never crowds a repairable one out of the batch. Ordered by id and
    /// read in pages: <paramref name="after"/> is the last id of the previous page, so the pass walks the whole backlog
    /// instead of re-reading its first <paramref name="take"/> rows every time.
    /// </summary>
    private async Task<List<StrandedCredential>> LoadStrandedAsync(Guid? after, int take, CancellationToken cancellationToken)
    {
        var rows = await StrandedWithSuccessor(after)
            .OrderBy(candidate => candidate.DeadId)
            .Take(take)
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        return rows.Select(row => new StrandedCredential(row.DeadId, row.SuccessorId!.Value)).ToList();
    }

    /// <summary>
    /// Every candidate after <paramref name="after"/> (all of them when null), unbounded: what <see cref="LoadStrandedAsync"/>
    /// pages through and <see cref="CountAsync"/> counts, so the two cannot disagree about what a candidate is.
    /// </summary>
    private IQueryable<StrandedRow> StrandedWithSuccessor(Guid? after = null)
    {
        var deadCredentials = _db.Credential.AsNoTracking().Where(c => (c.Status == CredentialStatus.Revoked || c.Status == CredentialStatus.Error) && c.OwnerUserId != null);

        if (after is Guid cursor) deadCredentials = deadCredentials.Where(c => c.Id.CompareTo(cursor) > 0);

        return deadCredentials
            .Where(dead => _db.Repository.Any(r => r.CredentialId == dead.Id && r.TeamId == dead.TeamId && r.ProviderInstanceId == dead.ProviderInstanceId && r.DeletedDate == null)
                || _db.ConnectionWebhook.Any(w => w.CredentialId == dead.Id && w.ProviderInstanceId == dead.ProviderInstanceId && WebhookRegistrationLifecycle.InService.Contains(w.RegistrationStatus)))
            .Select(dead => new StrandedRow
            {
                DeadId = dead.Id,
                SuccessorId = _db.Credential
                    .Where(c => c.TeamId == dead.TeamId && c.ProviderInstanceId == dead.ProviderInstanceId && c.OwnerUserId == dead.OwnerUserId && c.Status == CredentialStatus.Active && c.DeletedDate == null && c.CreatedDate >= (dead.Status == CredentialStatus.Revoked ? dead.LastModifiedDate : (dead.LastValidatedDate ?? dead.CreatedDate)))
                    .OrderByDescending(c => c.CreatedDate).ThenByDescending(c => c.Id)
                    .Select(c => (Guid?)c.Id)
                    .FirstOrDefault()
            })
            .Where(candidate => candidate.SuccessorId != null);
    }

    /// <summary>Move one dead credential's bindings and save them on their own, then hand its revived hooks to the registrar.</summary>
    private async Task<bool> RepairAsync(StrandedCredential candidate, CancellationToken cancellationToken)
    {
        try
        {
            var successor = await _db.Credential.SingleAsync(c => c.Id == candidate.SuccessorId, cancellationToken).ConfigureAwait(false);

            var adopted = await AdoptBindingsAsync(successor, new List<Guid> { candidate.DeadId }, cancellationToken).ConfigureAwait(false);

            await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await DispatchAfterCommitAsync(adopted.RevivedHookIds, cancellationToken).ConfigureAwait(false);

            return true;
        }
        finally
        {
            _db.ChangeTracker.Clear();
        }
    }

    /// <summary>Re-point every live binding of <paramref name="predecessorIds"/> at <paramref name="successor"/> as tracked changes. Returns what moved, and the group hooks now waiting for a registrar.</summary>
    private async Task<Adopted> AdoptBindingsAsync(Credential successor, List<Guid> predecessorIds, CancellationToken cancellationToken)
    {
        var repositories = await LoadBoundRepositoriesAsync(successor, predecessorIds, cancellationToken).ConfigureAwait(false);
        var hooks = await LoadBoundConnectionWebhooksAsync(successor, predecessorIds, cancellationToken).ConfigureAwait(false);

        foreach (var repository in repositories) Adopt(repository, successor);
        foreach (var hook in hooks) Adopt(hook, successor);

        if (repositories.Count + hooks.Count > 0)
            _logger.LogInformation("Moved {RepositoryCount} repositories and {ConnectionWebhookCount} group hooks from {OwnerUserId}'s disconnected credentials on provider instance {ProviderInstanceId} to credential {CredentialId}", repositories.Count, hooks.Count, successor.OwnerUserId, successor.ProviderInstanceId, successor.Id);

        return new Adopted(repositories.Count, hooks.Count, hooks.Where(h => h.RegistrationStatus == RepositoryWebhookRegistrationStatus.Pending).Select(h => h.Id).ToList());
    }

    private async Task<List<Repository>> LoadBoundRepositoriesAsync(Credential successor, List<Guid> predecessorIds, CancellationToken cancellationToken) =>
        await _db.Repository
            .Where(r => r.TeamId == successor.TeamId && r.ProviderInstanceId == successor.ProviderInstanceId && r.DeletedDate == null && r.CredentialId != null && predecessorIds.Contains(r.CredentialId.Value))
            .ToListAsync(cancellationToken).ConfigureAwait(false);

    /// <summary>The in-service group hooks registered through a predecessor — a hook records the credential it registers, retries and deletes through, so it is as stranded as a repository. A Cancelled one is retired and stays.</summary>
    private async Task<List<ConnectionWebhook>> LoadBoundConnectionWebhooksAsync(Credential successor, List<Guid> predecessorIds, CancellationToken cancellationToken) =>
        await _db.ConnectionWebhook
            .Where(w => w.ProviderInstanceId == successor.ProviderInstanceId && predecessorIds.Contains(w.CredentialId) && WebhookRegistrationLifecycle.InService.Contains(w.RegistrationStatus))
            .ToListAsync(cancellationToken).ConfigureAwait(false);

    /// <summary>Bind the live credential, and lift the Error the dead one's disconnect set (<see cref="Revive(Repository)"/>).</summary>
    private static void Adopt(Repository repository, Credential successor)
    {
        repository.CredentialId = successor.Id;

        Revive(repository);
    }

    /// <summary>The inverse of the disconnect cascade: lift the Error it set. Any other status (Paused, Unreachable) is the repository's own and stays.</summary>
    private static void Revive(Repository repository)
    {
        if (repository.Status != RepositoryStatus.Error) return;

        repository.Status = RepositoryStatus.Active;
        repository.LastError = null;
    }

    /// <summary>Bind the live credential, and revive a hook the dead one failed (<see cref="Revive(ConnectionWebhook)"/>).</summary>
    private static void Adopt(ConnectionWebhook hook, Credential successor)
    {
        hook.CredentialId = successor.Id;

        Revive(hook);
    }

    /// <summary>
    /// Revive a hook a dead credential failed — attempts reset, due now — exactly as a bind revives it: a reconnect is the
    /// operator intervention that fixes what buried it. Registered, Enqueued and Registering keep their state; they only
    /// need a live credential for their next call.
    /// </summary>
    private static void Revive(ConnectionWebhook hook)
    {
        if (!Revivable.Contains(hook.RegistrationStatus)) return;

        hook.RegistrationStatus = RepositoryWebhookRegistrationStatus.Pending;
        hook.Attempts = 0;
        hook.NextAttemptAt = DateTimeOffset.UtcNow;
    }

    /// <summary>Hand each waiting hook to its registrar once the move is durable: deferred to the commit inside a unit of work, at once after a save outside one. Connection hooks have no reconciler, so a Pending row nobody dispatches waits for a bind that may never come.</summary>
    private async Task DispatchAfterCommitAsync(List<Guid> hookIds, CancellationToken cancellationToken)
    {
        foreach (var hookId in hookIds)
            await _postCommit.RunAfterCommitAsync(ct => _hookDispatcher.DispatchAsync(hookId, ct), cancellationToken).ConfigureAwait(false);
    }

    private sealed record StrandedCredential(Guid DeadId, Guid SuccessorId) : ISweepCandidate
    {
        public Guid Id => DeadId;
    }

    private sealed record Adopted(int RepositoryCount, int ConnectionWebhookCount, List<Guid> RevivedHookIds);

    /// <summary>The query's row: a member-initialised class because an anonymous type cannot be returned from <see cref="StrandedWithSuccessor"/>.</summary>
    private sealed class StrandedRow
    {
        public Guid DeadId { get; init; }
        public Guid? SuccessorId { get; init; }
    }
}
