using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Messages.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CodeSpace.Core.Services.Webhooks.Registration;

/// <summary>
/// Four independent sweeps, one per stuck-state class. Order matters less than you'd think
/// — each sweep's CAS protects against racing a normal flow or another reconciler tick.
/// </summary>
public sealed class StuckWebhookRegistrationReconcilerService : IStuckWebhookRegistrationReconcilerService, IScopedDependency
{
    /// <summary>Threshold for "Pending too long" — past this, re-dispatch.</summary>
    public static readonly TimeSpan PendingStuckAfter = TimeSpan.FromMinutes(2);

    /// <summary>Threshold for "Enqueued but no worker picked up" — past this, revert to Pending.</summary>
    public static readonly TimeSpan EnqueuedStuckAfter = TimeSpan.FromMinutes(10);

    /// <summary>Threshold for "Registering but worker crashed" — past this, revert to Pending so the registrar can retry.</summary>
    public static readonly TimeSpan RegisteringStuckAfter = TimeSpan.FromMinutes(5);

    /// <summary>Batch size per sweep — bounds the work the reconciler can do in one tick so a backlog doesn't run forever.</summary>
    public const int BatchSize = 50;

    private readonly CodeSpaceDbContext _db;
    private readonly IRepositoryWebhookRegistrationDispatcher _dispatcher;
    private readonly ILogger<StuckWebhookRegistrationReconcilerService> _logger;

    public StuckWebhookRegistrationReconcilerService(CodeSpaceDbContext db, IRepositoryWebhookRegistrationDispatcher dispatcher, ILogger<StuckWebhookRegistrationReconcilerService> logger)
    {
        _db = db;
        _dispatcher = dispatcher;
        _logger = logger;
    }

    public async Task<StuckWebhookRegistrationReconcileSummary> ReconcileAsync(CancellationToken cancellationToken)
    {
        var revivedFromFailed = await ReviveDueFailedAsync(cancellationToken).ConfigureAwait(false);
        var revertedFromRegistering = await RevertStuckRegisteringAsync(cancellationToken).ConfigureAwait(false);
        var revertedFromEnqueued = await RevertStuckEnqueuedAsync(cancellationToken).ConfigureAwait(false);
        var redispatched = await RedispatchDuePendingAsync(cancellationToken).ConfigureAwait(false);

        var summary = new StuckWebhookRegistrationReconcileSummary
        {
            RedispatchedFromPending = redispatched,
            RevertedFromEnqueued = revertedFromEnqueued,
            RevertedFromRegistering = revertedFromRegistering,
            RevivedFromFailed = revivedFromFailed,
        };

        if (summary.Total > 0)
            _logger.LogInformation(
                "StuckWebhookRegistrationReconciler: redispatched={Redispatched}, reverted-enqueued={RevertedEnqueued}, reverted-registering={RevertedRegistering}, revived-failed={RevivedFailed}",
                summary.RedispatchedFromPending, summary.RevertedFromEnqueued, summary.RevertedFromRegistering, summary.RevivedFromFailed);

        return summary;
    }

    /// <summary>
    /// Pending older than threshold AND past next_attempt_at: call DispatchAsync. The
    /// dispatcher's own CAS prevents double-dispatch if a normal flow is racing us.
    /// </summary>
    private async Task<int> RedispatchDuePendingAsync(CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var threshold = now - PendingStuckAfter;

        var stuckIds = await _db.RepositoryWebhook.AsNoTracking()
            .Where(w => w.RegistrationStatus == RepositoryWebhookRegistrationStatus.Pending
                     && w.CreatedDate < threshold
                     && w.NextAttemptAt <= now)
            .OrderBy(w => w.CreatedDate)
            .Take(BatchSize)
            .Select(w => w.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var redispatched = 0;
        foreach (var webhookId in stuckIds)
        {
            try
            {
                if (await _dispatcher.DispatchAsync(webhookId, cancellationToken).ConfigureAwait(false)) redispatched++;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "StuckWebhookRegistrationReconciler: re-dispatch failed for webhook {WebhookId}; will retry next tick", webhookId);
            }
        }

        return redispatched;
    }

    /// <summary>
    /// Enqueued older than threshold: walk back to Pending via CAS. Hangfire lost the job
    /// (storage outage, queue mis-routing, etc.). The CAS WHERE clause ensures we don't
    /// trample a worker that's just flipping Enqueued → Registering right now.
    /// </summary>
    private async Task<int> RevertStuckEnqueuedAsync(CancellationToken cancellationToken)
    {
        var threshold = DateTimeOffset.UtcNow - EnqueuedStuckAfter;

        return await _db.RepositoryWebhook
            .Where(w => w.RegistrationStatus == RepositoryWebhookRegistrationStatus.Enqueued
                     && w.EnqueuedAt != null && w.EnqueuedAt < threshold)
            .ExecuteUpdateAsync(s => s
                .SetProperty(w => w.RegistrationStatus, RepositoryWebhookRegistrationStatus.Pending)
                .SetProperty(w => w.EnqueuedAt, (DateTimeOffset?)null), cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Registering older than threshold: revert to Pending so the registrar can re-fire. The
    /// registrar's idempotency check (FindWebhookByCallbackUrlAsync) covers the case where
    /// the provider call actually landed before the worker crashed — the next run sees the
    /// existing remote hook and writes external_id without creating a duplicate.
    /// </summary>
    private async Task<int> RevertStuckRegisteringAsync(CancellationToken cancellationToken)
    {
        var threshold = DateTimeOffset.UtcNow - RegisteringStuckAfter;

        return await _db.RepositoryWebhook
            .Where(w => w.RegistrationStatus == RepositoryWebhookRegistrationStatus.Registering
                     && w.RegisteringAt != null && w.RegisteringAt < threshold)
            .ExecuteUpdateAsync(s => s
                .SetProperty(w => w.RegistrationStatus, RepositoryWebhookRegistrationStatus.Pending)
                .SetProperty(w => w.RegisteringAt, (DateTimeOffset?)null), cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Failed rows whose backoff window has elapsed: flip back to Pending so the dispatcher
    /// picks them up. The CAS WHERE keeps DeadLettered + Cancelled rows untouched.
    /// </summary>
    private async Task<int> ReviveDueFailedAsync(CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;

        return await _db.RepositoryWebhook
            .Where(w => w.RegistrationStatus == RepositoryWebhookRegistrationStatus.Failed
                     && w.NextAttemptAt <= now)
            .ExecuteUpdateAsync(s => s
                .SetProperty(w => w.RegistrationStatus, RepositoryWebhookRegistrationStatus.Pending), cancellationToken)
            .ConfigureAwait(false);
    }
}
