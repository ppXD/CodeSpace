using System.Security.Cryptography;
using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Credentials;
using CodeSpace.Core.Services.Providers;
using CodeSpace.Core.Services.Providers.Capabilities;
using CodeSpace.Core.Services.Providers.Events;
using CodeSpace.Core.Settings.Webhooks;
using CodeSpace.Messages.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CodeSpace.Core.Services.Webhooks.Registration;

public sealed class ConnectionWebhookProvisioner : IConnectionWebhookProvisioner, IScopedDependency
{
    private readonly CodeSpaceDbContext _db;
    private readonly IProviderRegistry _registry;
    private readonly IProviderEventSubscriptionRegistry _subscriptionRegistry;
    private readonly IPayloadEncryptor _encryptor;
    private readonly WebhookBaseUrlSetting _webhookBaseUrl;
    private readonly ILogger<ConnectionWebhookProvisioner> _logger;

    public ConnectionWebhookProvisioner(CodeSpaceDbContext db, IProviderRegistry registry, IProviderEventSubscriptionRegistry subscriptionRegistry, IPayloadEncryptor encryptor, WebhookBaseUrlSetting webhookBaseUrl, ILogger<ConnectionWebhookProvisioner> logger)
    {
        _db = db;
        _registry = registry;
        _subscriptionRegistry = subscriptionRegistry;
        _encryptor = encryptor;
        _webhookBaseUrl = webhookBaseUrl;
        _logger = logger;
    }

    public async Task<Guid?> EnsureForOwnerAsync(ProviderInstance instance, Guid credentialId, string ownerPath, CancellationToken cancellationToken)
    {
        var covering = await LoadCoveringHookAsync(instance.Id, ownerPath, cancellationToken).ConfigureAwait(false);

        if (covering != null) return await ReviveIfDueAsync(covering, cancellationToken).ConfigureAwait(false);

        var staged = StageNewHook(instance, credentialId, ownerPath);

        await RetireCoveredDescendantsAsync(instance, ownerPath, cancellationToken).ConfigureAwait(false);

        return staged;
    }

    public async Task<int> RetireAllAsync(ProviderInstance instance, CancellationToken cancellationToken)
    {
        var hooks = await _db.ConnectionWebhook
            .Include(w => w.Credential)
            .Where(w => w.ProviderInstanceId == instance.Id && WebhookRegistrationLifecycle.InService.Contains(w.RegistrationStatus))
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        if (hooks.Count == 0) return 0;

        await RetireAsync(instance, hooks, cancellationToken).ConfigureAwait(false);

        return hooks.Count;
    }

    /// <summary>
    /// Take these hooks out of service: delete each at the provider best-effort, drop the Registered
    /// rows (they described a hook that no longer exists, so keeping them would claim something
    /// untrue), and CAS the rest to Cancelled so a registration still in flight cannot complete
    /// behind us.
    /// </summary>
    private async Task RetireAsync(ProviderInstance instance, List<ConnectionWebhook> hooks, CancellationToken cancellationToken)
    {
        await BestEffortDeleteRemoteAsync(instance, hooks, cancellationToken).ConfigureAwait(false);

        var registered = hooks.Where(h => h.RegistrationStatus == RepositoryWebhookRegistrationStatus.Registered).ToList();
        _db.ConnectionWebhook.RemoveRange(registered);

        await CancelNonTerminalAsync(hooks.Select(h => h.Id).ToList(), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The nearest hook at or above this owner, nearest first. A group hook covers every project in
    /// the group AND in its subgroups, so a hook on <c>acme/platform</c> already covers
    /// <c>acme/platform/web</c> — registering a second one there is not redundancy, it is every push
    /// in the subgroup arriving twice and starting two runs.
    /// </summary>
    private async Task<ConnectionWebhook?> LoadCoveringHookAsync(Guid providerInstanceId, string ownerPath, CancellationToken cancellationToken)
    {
        var candidatePaths = OwnerPathHierarchy.SelfAndAncestors(ownerPath);

        var live = await _db.ConnectionWebhook
            .Where(w => w.ProviderInstanceId == providerInstanceId && candidatePaths.Contains(w.OwnerPath) && WebhookRegistrationLifecycle.InService.Contains(w.RegistrationStatus))
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        return candidatePaths.Select(path => live.FirstOrDefault(w => w.OwnerPath == path)).FirstOrDefault(hook => hook != null);
    }

    /// <summary>
    /// The reverse of the ancestor rule, and required by it: registering at <c>acme</c> when
    /// <c>acme/platform</c> already has a hook must take the descendant OUT, or the subgroup's pushes
    /// arrive on both and start two runs — the exact duplication the ancestor rule prevents in the
    /// other direction. Leaving them live and calling it belt-and-braces would make the mode's one
    /// promise ("never two hooks covering one repository") false in half the orderings.
    /// </summary>
    private async Task RetireCoveredDescendantsAsync(ProviderInstance instance, string ownerPath, CancellationToken cancellationToken)
    {
        var live = await _db.ConnectionWebhook
            .Include(w => w.Credential)
            .Where(w => w.ProviderInstanceId == instance.Id && w.OwnerPath != ownerPath && WebhookRegistrationLifecycle.InService.Contains(w.RegistrationStatus))
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        var covered = live.Where(w => OwnerPathHierarchy.Covers(ownerPath, w.OwnerPath)).ToList();

        if (covered.Count == 0) return;

        _logger.LogInformation("ConnectionWebhookProvisioner: hook on {OwnerPath} covers {Count} narrower hooks on {ProviderInstanceId} — retiring them", ownerPath, covered.Count, instance.Id);

        await RetireAsync(instance, covered, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Stage the durable intent — encrypted secret, committed callback URL — in the caller's unit of
    /// work, exactly as a bind stages a per-repository hook. If the process dies before the id is
    /// dispatched, the row is what lets the next bind under this owner pick it up.
    /// </summary>
    private Guid StageNewHook(ProviderInstance instance, Guid credentialId, string ownerPath)
    {
        var id = Guid.NewGuid();
        var secret = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

        _db.ConnectionWebhook.Add(new ConnectionWebhook
        {
            Id = id,
            ProviderInstanceId = instance.Id,
            CredentialId = credentialId,
            OwnerPath = ownerPath,
            ExternalId = null,
            CallbackUrl = BuildCallbackUrl(id),
            SecretEnc = _encryptor.Encrypt(secret),
            SubscribedEvents = _subscriptionRegistry.GetSubscribedRawEvents(instance.Provider).ToList(),
            Active = true,
            RegistrationStatus = RepositoryWebhookRegistrationStatus.Pending,
            NextAttemptAt = DateTimeOffset.UtcNow
        });

        return id;
    }

    /// <summary>
    /// A row already covers this owner. Pending is already dispatchable and Enqueued/Registering/
    /// Registered are working or done, so the two worth acting on are Failed-and-due and
    /// DeadLettered. The CAS is what keeps this safe against a sweep doing the same.
    ///
    /// <para>DeadLettered has to be revivable HERE, and only here, because it is the one in-service
    /// state nothing else can leave on its own: the reconciler deliberately skips it, and connection
    /// hooks have no per-hook retry endpoint the way repository hooks do. Counting it as covering —
    /// which the duplicate-hook rule requires — without this branch would wedge the group, since
    /// every later bind would find it, decline to act, and stage nothing.</para>
    ///
    /// <para>A bind IS the operator intervention DeadLettered asks for: somebody is actively asking
    /// for this owner to be covered, usually right after fixing the credential or the plan that
    /// buried it. So attempts reset, exactly as the per-repository retry resets them — a row already
    /// at MaxAttempts revived without the reset buys one try, and the next transient timeout puts it
    /// straight back. The count is a position on the backoff ladder, not a census; the census is the
    /// append-only attempt timeline, which this does not touch.</para>
    /// </summary>
    private async Task<Guid?> ReviveIfDueAsync(ConnectionWebhook existing, CancellationToken cancellationToken)
    {
        if (existing.RegistrationStatus == RepositoryWebhookRegistrationStatus.Pending) return existing.Id;

        if (existing.RegistrationStatus == RepositoryWebhookRegistrationStatus.DeadLettered)
            return await ReviveAsync(existing.Id, RepositoryWebhookRegistrationStatus.DeadLettered, resetAttempts: true, cancellationToken).ConfigureAwait(false);

        if (existing.RegistrationStatus != RepositoryWebhookRegistrationStatus.Failed) return null;

        // Failed rows are on the backoff ladder and the reconciler is already walking them, so this
        // only pulls one forward when it is due — and leaves the ladder position alone.
        if (existing.NextAttemptAt > DateTimeOffset.UtcNow) return null;

        return await ReviveAsync(existing.Id, RepositoryWebhookRegistrationStatus.Failed, resetAttempts: false, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>CAS the row back to Pending, guarded on the state we read so a reconciler tick that got there first wins and this becomes a no-op.</summary>
    private async Task<Guid?> ReviveAsync(Guid hookId, RepositoryWebhookRegistrationStatus observed, bool resetAttempts, CancellationToken cancellationToken)
    {
        var revived = await _db.ConnectionWebhook
            .Where(w => w.Id == hookId && w.RegistrationStatus == observed)
            .ExecuteUpdateAsync(s => s
                .SetProperty(w => w.RegistrationStatus, RepositoryWebhookRegistrationStatus.Pending)
                .SetProperty(w => w.Attempts, w => resetAttempts ? 0 : w.Attempts)
                .SetProperty(w => w.NextAttemptAt, DateTimeOffset.UtcNow), cancellationToken)
            .ConfigureAwait(false);

        return revived == 0 ? null : hookId;
    }

    private async Task BestEffortDeleteRemoteAsync(ProviderInstance instance, List<ConnectionWebhook> hooks, CancellationToken cancellationToken)
    {
        if (!_registry.TryGet<IConnectionWebhookRegistrationCapability>(instance.Provider, out var capability)) return;

        foreach (var hook in hooks.Where(h => !string.IsNullOrEmpty(h.ExternalId)))
        {
            try
            {
                await capability!.DeleteConnectionWebhookAsync(new ProviderContext(instance, hook.Credential), hook.OwnerPath, hook.ExternalId!, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // A hook we cannot delete is a hook the operator has to remove by hand, which is
                // worth a warning and is not worth blocking the switch: leaving the local row would
                // make the connection claim a mode it is no longer in.
                _logger.LogWarning(ex, "Failed to delete remote connection webhook {ConnectionWebhookId} on {OwnerPath}; removing the local record anyway", hook.Id, hook.OwnerPath);
            }
        }
    }

    /// <summary>
    /// Atomically take exactly these rows out of the lifecycle. The CAS beats a registrar mid-call:
    /// its completion CAS then finds the row no longer Registering and no-ops.
    ///
    /// <para>Scoped to the ids that were selected for retirement, never to the whole connection —
    /// a retirement of the descendants a new hook swallows runs while that new hook is staged, and
    /// a connection-wide CAS would cancel the very row the caller is about to dispatch.</para>
    /// </summary>
    private async Task CancelNonTerminalAsync(List<Guid> hookIds, CancellationToken cancellationToken) =>
        await _db.ConnectionWebhook
            .Where(w => hookIds.Contains(w.Id) && WebhookRegistrationLifecycle.RetirableToCancelled.Contains(w.RegistrationStatus))
            .ExecuteUpdateAsync(s => s.SetProperty(w => w.RegistrationStatus, RepositoryWebhookRegistrationStatus.Cancelled), cancellationToken)
            .ConfigureAwait(false);

    private string BuildCallbackUrl(Guid connectionWebhookId) => $"{_webhookBaseUrl.Value.TrimEnd('/')}/api/webhooks/connection/{connectionWebhookId}";
}
