namespace CodeSpace.Core.Services.Workflows.Reconciliation;

/// <summary>
/// Recovers <see cref="Persistence.Entities.WorkflowRun"/> rows that drifted out of their
/// expected state-machine trajectory. The dispatcher's CAS + the engine's CAS give us
/// no-double-execution; the reconciler is what gives us no-stuck-rows.
///
/// <para>Three failure modes covered:</para>
/// <list type="number">
///   <item><b>Stuck Pending</b> (created > 2 min ago, never advanced) — process crashed
///       between RunStarter.SaveChanges and IWorkflowRunDispatcher.DispatchAsync, OR the
///       dispatch threw mid-Hangfire-enqueue and reverted the row to Pending. Re-dispatch
///       is idempotent (CAS races a normal dispatcher safely).</item>
///   <item><b>Stuck Enqueued</b> (transitioned > 10 min ago, no worker picked up) — Hangfire
///       lost the job (storage outage, queue mis-routing, etc.). Revert the row to Pending
///       via CAS WHERE status='Enqueued' so the next tick re-dispatches it. The CAS
///       protects against racing a worker that's just now flipping to Running.</item>
///   <item><b>Abandoned Running</b> (status=Running, no ledger activity in 5 min, started
///       > 30 min ago) — engine crashed mid-run, OR worker host died, OR the run is hung
///       on an external call with no timeout. Mark Failure with an "abandoned" error so the
///       operator sees what happened + can Replay. We CANNOT auto-replay because the original
///       worker may have made side-effecting calls (POST comment, send Slack); replaying
///       would duplicate those.</item>
/// </list>
///
/// <para>Idempotent + safe to call concurrently from multiple replicas because every state
/// transition is an atomic CAS.</para>
/// </summary>
public interface IStuckRunReconcilerService
{
    Task<StuckRunReconcileSummary> ReconcileAsync(CancellationToken cancellationToken);
}

/// <summary>Diagnostic summary of one reconcile sweep. Returned for log surfacing + the recurring-job result.</summary>
public sealed record StuckRunReconcileSummary
{
    public int RedispatchedFromPending { get; init; }
    public int RevertedFromEnqueued { get; init; }
    public int MarkedAbandonedFromRunning { get; init; }

    public int Total => RedispatchedFromPending + RevertedFromEnqueued + MarkedAbandonedFromRunning;
}
