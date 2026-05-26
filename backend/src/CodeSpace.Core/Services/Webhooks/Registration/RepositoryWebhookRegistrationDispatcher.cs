using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Services.Jobs;
using CodeSpace.Messages.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CodeSpace.Core.Services.Webhooks.Registration;

/// <summary>
/// Atomic CAS + revert-on-throw, isolated from <see cref="RepositoryBinding.RepositoryBindingService"/>
/// and the reconciler. Mirror of <c>WorkflowRunDispatcher</c>.
/// </summary>
public sealed class RepositoryWebhookRegistrationDispatcher : IRepositoryWebhookRegistrationDispatcher, IScopedDependency
{
    private readonly CodeSpaceDbContext _db;
    private readonly ICodeSpaceBackgroundJobClient _backgroundJobClient;
    private readonly ILogger<RepositoryWebhookRegistrationDispatcher> _logger;

    public RepositoryWebhookRegistrationDispatcher(CodeSpaceDbContext db, ICodeSpaceBackgroundJobClient backgroundJobClient, ILogger<RepositoryWebhookRegistrationDispatcher> logger)
    {
        _db = db;
        _backgroundJobClient = backgroundJobClient;
        _logger = logger;
    }

    public async Task<bool> DispatchAsync(Guid webhookId, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;

        // Atomic CAS — Pending → Enqueued. The single UPDATE ... WHERE registration_status = 'Pending'
        // is the no-double-dispatch primitive: two concurrent callers (BindAsync + a reconciler
        // re-dispatching the same row) cannot both succeed. Postgres's row-level lock plus the
        // WHERE clause guarantees exactly one UPDATE affects 1 row, the other 0. EnqueuedAt
        // is set in the same statement so any subsequent reader sees a coherent (Enqueued, time)
        // pair without a second round-trip.
        var transitioned = await _db.RepositoryWebhook
            .Where(w => w.Id == webhookId && w.RegistrationStatus == RepositoryWebhookRegistrationStatus.Pending)
            .ExecuteUpdateAsync(s => s
                .SetProperty(w => w.RegistrationStatus, RepositoryWebhookRegistrationStatus.Enqueued)
                .SetProperty(w => w.EnqueuedAt, (DateTimeOffset?)now), cancellationToken)
            .ConfigureAwait(false);

        if (transitioned == 0)
        {
            // Not in Pending — lost the race, terminal (Registered / DeadLettered / Cancelled),
            // or someone else already advanced it. Caller silently returns; the reconciler
            // treats this as "already handled" and skips.
            _logger.LogDebug("RepositoryWebhookRegistrationDispatcher: webhook {WebhookId} not in Pending state — skipping dispatch", webhookId);
            return false;
        }

        // Hand to background-job client. From here until Enqueue returns the row sits in
        // Enqueued. If the process dies HERE, the row stays Enqueued forever; the reconciler
        // covers that case via a longer threshold (Enqueued + last_modified < now-10min →
        // revert to Pending so the next dispatcher tick re-fires).
        try
        {
            var jobId = _backgroundJobClient.Enqueue<IRepositoryWebhookRegistrar>(r => r.RunAsync(webhookId, CancellationToken.None));
            _logger.LogInformation("RepositoryWebhookRegistrationDispatcher: webhook {WebhookId} enqueued as background job {JobId}", webhookId, jobId);
            return true;
        }
        catch (Exception ex)
        {
            // Revert on failure: background-job client threw (Hangfire storage unreachable,
            // expression serialization bug, etc.). Walk the row back to Pending so the
            // reconciler picks it up + the next tick retries.
            //
            // The revert is itself a CAS: only flip Enqueued → Pending, never overwrite a row
            // that's somehow already advanced. CancellationToken.None — we want the revert to
            // land even if the caller's cancellation token has tripped, otherwise a cancelled
            // caller leaves an orphaned Enqueued row.
            _logger.LogWarning(ex, "RepositoryWebhookRegistrationDispatcher: enqueue failed for webhook {WebhookId}; reverting to Pending", webhookId);

            await _db.RepositoryWebhook
                .Where(w => w.Id == webhookId && w.RegistrationStatus == RepositoryWebhookRegistrationStatus.Enqueued)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(w => w.RegistrationStatus, RepositoryWebhookRegistrationStatus.Pending)
                    .SetProperty(w => w.EnqueuedAt, (DateTimeOffset?)null), CancellationToken.None)
                .ConfigureAwait(false);

            throw;
        }
    }
}
