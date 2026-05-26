using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Services.Workflows.Dispatch;
using CodeSpace.Core.Services.Workflows.Lifecycle;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CodeSpace.Core.Services.Workflows.Reconciliation;

/// <summary>
/// Three independent sweeps, one per stuck-state class. Order matters less than you'd think
/// — each sweep's CAS protects against racing a normal flow or another reconciler tick.
/// </summary>
public sealed class StuckRunReconcilerService : IStuckRunReconcilerService, IScopedDependency
{
    /// <summary>Threshold for "Pending too long" — past this, re-dispatch.</summary>
    public static readonly TimeSpan PendingStuckAfter = TimeSpan.FromMinutes(2);

    /// <summary>Threshold for "Enqueued but no worker picked up" — past this, revert to Pending.</summary>
    public static readonly TimeSpan EnqueuedStuckAfter = TimeSpan.FromMinutes(10);

    /// <summary>Threshold for "Running but no progress" — past this AND no recent ledger activity, mark Failure.</summary>
    public static readonly TimeSpan RunningStuckAfter = TimeSpan.FromMinutes(30);

    /// <summary>"Recent" ledger activity window — if a run has emitted records within this window, treat it as alive.</summary>
    public static readonly TimeSpan LedgerLivenessWindow = TimeSpan.FromMinutes(5);

    /// <summary>Batch size per sweep — bounds the work the reconciler can do in one tick so a backlog doesn't run forever.</summary>
    public const int BatchSize = 50;

    private readonly CodeSpaceDbContext _db;
    private readonly IWorkflowRunDispatcher _dispatcher;
    private readonly IRunRecordLogger _recordLogger;
    private readonly ILogger<StuckRunReconcilerService> _logger;

    public StuckRunReconcilerService(CodeSpaceDbContext db, IWorkflowRunDispatcher dispatcher, IRunRecordLogger recordLogger, ILogger<StuckRunReconcilerService> logger)
    {
        _db = db;
        _dispatcher = dispatcher;
        _recordLogger = recordLogger;
        _logger = logger;
    }

    public async Task<StuckRunReconcileSummary> ReconcileAsync(CancellationToken cancellationToken)
    {
        var redispatched = await RedispatchStuckPendingAsync(cancellationToken).ConfigureAwait(false);
        var reverted = await RevertStuckEnqueuedAsync(cancellationToken).ConfigureAwait(false);
        var abandoned = await MarkAbandonedRunningAsync(cancellationToken).ConfigureAwait(false);

        var summary = new StuckRunReconcileSummary
        {
            RedispatchedFromPending = redispatched,
            RevertedFromEnqueued = reverted,
            MarkedAbandonedFromRunning = abandoned,
        };

        if (summary.Total > 0)
            _logger.LogInformation(
                "StuckRunReconciler: redispatched={Redispatched}, reverted={Reverted}, abandoned={Abandoned}",
                summary.RedispatchedFromPending, summary.RevertedFromEnqueued, summary.MarkedAbandonedFromRunning);

        return summary;
    }

    /// <summary>
    /// Pending older than threshold: call DispatchAsync. The dispatcher's own CAS prevents
    /// double-dispatch if a normal flow is racing us; we just hand the id back into the
    /// queue and Hangfire takes it from there.
    /// </summary>
    private async Task<int> RedispatchStuckPendingAsync(CancellationToken cancellationToken)
    {
        var threshold = DateTimeOffset.UtcNow - PendingStuckAfter;

        var stuckIds = await _db.WorkflowRun.AsNoTracking()
            .Where(r => r.Status == WorkflowRunStatus.Pending && r.CreatedDate < threshold)
            .OrderBy(r => r.CreatedDate)
            .Take(BatchSize)
            .Select(r => r.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var redispatched = 0;
        foreach (var runId in stuckIds)
        {
            try
            {
                if (await _dispatcher.DispatchAsync(runId, cancellationToken).ConfigureAwait(false)) redispatched++;
            }
            catch (Exception ex)
            {
                // Per-row resilience: one stuck row's failure doesn't abort the sweep.
                _logger.LogWarning(ex, "StuckRunReconciler: re-dispatch failed for run {RunId}; will retry next tick", runId);
            }
        }

        return redispatched;
    }

    /// <summary>
    /// Enqueued older than threshold: walk back to Pending via CAS. The CAS WHERE clause
    /// ensures we don't trample a worker that's just flipping to Running right now.
    /// </summary>
    private async Task<int> RevertStuckEnqueuedAsync(CancellationToken cancellationToken)
    {
        var threshold = DateTimeOffset.UtcNow - EnqueuedStuckAfter;

        return await _db.WorkflowRun
            .Where(r => r.Status == WorkflowRunStatus.Enqueued && r.LastModifiedDate < threshold)
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.Status, WorkflowRunStatus.Pending), cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Running older than threshold AND no recent ledger activity: mark Failure. We check
    /// the ledger to distinguish "worker crashed" (no activity) from "long-running but alive"
    /// (recent activity from a slow LLM call etc.). Marking Failure inline is safe HERE
    /// (unlike the engine entry, where it'd corrupt mid-execution) because the duration
    /// heuristic gives us high confidence the worker is gone.
    /// </summary>
    private async Task<int> MarkAbandonedRunningAsync(CancellationToken cancellationToken)
    {
        var runningThreshold = DateTimeOffset.UtcNow - RunningStuckAfter;
        var livenessThreshold = DateTimeOffset.UtcNow - LedgerLivenessWindow;

        // Find Running runs whose latest ledger record is older than the liveness window
        // (or has no ledger at all). The outer join via a sub-select keeps this to a single
        // round-trip; the resulting IDs are the candidates we mark.
        var candidates = await (
            from run in _db.WorkflowRun.AsNoTracking()
            where run.Status == WorkflowRunStatus.Running && run.StartedAt < runningThreshold
            let mostRecentLedger = _db.WorkflowRunRecord.AsNoTracking()
                .Where(rec => rec.RunId == run.Id)
                .Max(rec => (DateTimeOffset?)rec.OccurredAt)
            where mostRecentLedger == null || mostRecentLedger < livenessThreshold
            orderby run.StartedAt
            select run.Id
        ).Take(BatchSize).ToListAsync(cancellationToken).ConfigureAwait(false);

        var marked = 0;
        foreach (var runId in candidates)
        {
            // Atomic transition Running → Failure. Pinned to status=Running so a Cancel that
            // raced us doesn't get overwritten (Cancelled is also terminal; respect it).
            var now = DateTimeOffset.UtcNow;
            const string AbandonedError = "Run marked abandoned by reconciler — worker crashed or hung past " +
                                          "the abandoned-run threshold with no ledger progress. Replay the run to retry; " +
                                          "side-effecting nodes from the original run will NOT be re-fired.";

            var transitioned = await _db.WorkflowRun
                .Where(r => r.Id == runId && r.Status == WorkflowRunStatus.Running)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(r => r.Status, WorkflowRunStatus.Failure)
                    .SetProperty(r => r.Error, AbandonedError)
                    .SetProperty(r => r.CompletedAt, (DateTimeOffset?)now), cancellationToken)
                .ConfigureAwait(false);

            if (transitioned == 0) continue;

            marked++;

            // Emit a run.failed ledger record so the run-detail UI surfaces the abandoned-by-
            // reconciler decision in the timeline. Failure to write the record doesn't undo
            // the status transition — we logged the warning + the operator can grep the run.
            try
            {
                var startedAt = await _db.WorkflowRun.AsNoTracking().Where(r => r.Id == runId).Select(r => r.StartedAt).SingleAsync(cancellationToken).ConfigureAwait(false);
                var duration = startedAt.HasValue ? now - startedAt.Value : TimeSpan.Zero;
                await _recordLogger.RunFailedAsync(runId, AbandonedError, duration, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "StuckRunReconciler: failed to emit run.failed ledger for abandoned run {RunId}", runId);
            }
        }

        return marked;
    }
}
