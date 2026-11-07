using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Services.Jobs;
using CodeSpace.Core.Services.Workflows.Engine;
using CodeSpace.Messages.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CodeSpace.Core.Services.Workflows.Dispatch;

/// <summary>
/// Atomic CAS + revert-on-throw isolated from the three call sites (manual run, replay run,
/// webhook dispatcher), behind a refusal of any sub-workflow child whose parent run has finished.
/// </summary>
public sealed class WorkflowRunDispatcher : IWorkflowRunDispatcher, IScopedDependency
{
    private readonly CodeSpaceDbContext _db;
    private readonly ICodeSpaceBackgroundJobClient _backgroundJobClient;
    private readonly ILogger<WorkflowRunDispatcher> _logger;

    public WorkflowRunDispatcher(CodeSpaceDbContext db, ICodeSpaceBackgroundJobClient backgroundJobClient, ILogger<WorkflowRunDispatcher> logger)
    {
        _db = db;
        _backgroundJobClient = backgroundJobClient;
        _logger = logger;
    }

    public async Task<bool> DispatchAsync(Guid runId, CancellationToken cancellationToken)
    {
        if (await HasFinishedAncestorAsync(runId, cancellationToken).ConfigureAwait(false)) return RefuseUnderFinishedAncestor(runId);

        // Atomic CAS — Pending → Enqueued.
        // ExecuteUpdateAsync emits a single UPDATE ... WHERE Status = Pending statement.
        // Two concurrent callers (e.g. RunManuallyAsync + a reconciler that found the same
        // row) cannot both succeed: Postgres's row-level lock + the WHERE clause guarantees
        // exactly one UPDATE affects 1 row, the other affects 0. No double-enqueue possible.
        //
        // We ALSO set EnqueuedAt in the same UPDATE — this is the canonical "when did this
        // run enter the queue" timestamp. The reconciler's stuck-Enqueued sweep reads
        // EnqueuedAt rather than LastModifiedDate because ExecuteUpdateAsync bypasses EF's
        // audit hook (LastModifiedDate would still reflect the original Pending-state value,
        // making a freshly-enqueued run look instantly stale).
        var enqueuedAt = DateTimeOffset.UtcNow;
        var transitioned = await _db.WorkflowRun
            .Where(r => r.Id == runId && r.Status == WorkflowRunStatus.Pending)
            .ExecuteUpdateAsync(s => s
                .SetProperty(r => r.Status, WorkflowRunStatus.Enqueued)
                .SetProperty(r => r.EnqueuedAt, (DateTimeOffset?)enqueuedAt), cancellationToken)
            .ConfigureAwait(false);

        if (transitioned == 0)
        {
            // Not in Pending state — either we lost the race, the run is already terminal,
            // or someone else already picked it up. All cases: caller should silently move
            // on. Reconciler treats this as "already handled" and skips.
            _logger.LogDebug("WorkflowRunDispatcher: run {RunId} not in Pending state — skipping dispatch", runId);
            return false;
        }

        // Hand to background-job client.
        // From this point until Enqueue returns, the row is in Enqueued state. If the process
        // dies HERE, the row stays Enqueued forever — the reconciler covers this case via a
        // longer threshold (Enqueued + last_modified < now-10min → re-dispatch).
        try
        {
            var jobId = _backgroundJobClient.Enqueue<IWorkflowEngine>(e => e.ExecuteRunAsync(runId, CancellationToken.None));
            _logger.LogInformation("WorkflowRunDispatcher: run {RunId} enqueued as background job {JobId}", runId, jobId);
            return true;
        }
        catch (Exception ex)
        {
            // Revert on failure: background-job client threw (Hangfire storage unreachable,
            // expression serialization bug, etc.). Walk the row back to Pending so the
            // reconciler picks it up + the next tick retries.
            //
            // The revert is itself a CAS: only flip Enqueued → Pending, never overwrite a row
            // that's somehow already advanced.
            _logger.LogWarning(ex, "WorkflowRunDispatcher: enqueue failed for run {RunId}; reverting to Pending", runId);

            // Use CancellationToken.None for the revert — we want it to land even if the
            // caller's cancellation token has tripped, otherwise a cancelled caller leaves
            // an orphaned Enqueued row. The revert clears EnqueuedAt so the reconciler's
            // sweep window restarts from the next successful dispatch.
            await _db.WorkflowRun
                .Where(r => r.Id == runId && r.Status == WorkflowRunStatus.Enqueued)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(r => r.Status, WorkflowRunStatus.Pending)
                    .SetProperty(r => r.EnqueuedAt, (DateTimeOffset?)null), CancellationToken.None)
                .ConfigureAwait(false);

            throw;
        }
    }

    /// <summary>
    /// Whether the run is a sub-workflow child whose ancestry has finished. Every resume, revive and reconciler re-dispatch comes
    /// through here, so this is the last door before a worker walks the run: a child whose parent has ended — a timer still firing
    /// after the parent failed, say — would run toward a wait nothing holds open any more, spending for no one.
    /// </summary>
    private async Task<bool> HasFinishedAncestorAsync(Guid runId, CancellationToken cancellationToken) =>
        await RunAncestry.HasFinishedAncestorAsync(runId, id => LoadHopAsync(id, cancellationToken)).ConfigureAwait(false);

    private async Task<RunAncestry.Hop?> LoadHopAsync(Guid runId, CancellationToken cancellationToken) =>
        await _db.WorkflowRun.AsNoTracking().Where(r => r.Id == runId).Select(r => new RunAncestry.Hop(r.SourceType, r.ParentRunId, r.Status)).SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);

    /// <summary>Leave the run as it is — Pending, if a resume flipped it — for the stuck-run reconciler, which cancels a sub-workflow child no live parent waits for.</summary>
    private bool RefuseUnderFinishedAncestor(Guid runId)
    {
        _logger.LogInformation("WorkflowRunDispatcher: run {RunId} not dispatched — it is a sub-workflow whose parent run has finished, so nothing is waiting for it", runId);
        return false;
    }
}
