namespace CodeSpace.Core.Services.Workflows.Reconciliation;

/// <summary>
/// Recovers <see cref="Persistence.Entities.WorkflowRun"/> rows that drifted out of their
/// expected state-machine trajectory. The dispatcher's CAS + the engine's CAS give us
/// no-double-execution; the reconciler is what gives us no-stuck-rows.
///
/// <para>Four failure modes covered:</para>
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
///   <item><b>Stranded Suspended</b> (status=Suspended > 2 min ago with ZERO pending waits) —
///       a resume resolved its wait in the narrow window AFTER an in-flight re-walk had already
///       passed that branch node, so the walk re-suspended the run while the resolver's
///       Suspended→Pending flip became a no-op (the run was momentarily Running). The run ends
///       Suspended with ALL waits Resolved and no dispatch coming — stranded forever. A legitimately
///       parked run always has a Pending wait, so it is excluded; we CAS Suspended→Pending then
///       re-dispatch (the resolved waits rehydrate as the suspended nodes' payloads on the re-walk).</item>
///   <item><b>Supervisor self-advance lost</b> (status=Suspended > 2 min ago with a pending
///       <c>SupervisorDecision</c> wait — PR-E E2, flag-gated) — a supervisor turn parks on a wait that
///       self-advances (no external work item), but the post-Suspended-commit <c>ResumeWaitAsync</c>
///       enqueue was lost (a crash between commit and enqueue, or a dropped Hangfire job). Unlike every
///       other wait this run will NEVER be woken externally, and the Stranded sweep excludes it (it HAS a
///       pending wait), so the reconciler re-fires the self-advance. The resume's own wait-status CAS makes
///       a re-fire racing a still-live original idempotent. Sweep runs ONLY when the supervisor lane is
///       enabled, so a flag-OFF deployment is byte-identical (no extra query).</item>
///   <item><b>Abandoned-Running supervisor run</b> (status=Running, no ledger activity in 5 min, started
///       > 30 min ago, AND a non-terminal <c>SupervisorDecisionRecord</c> — PR-E P1-2, flag-gated) — a worker
///       died mid-supervisor-decision, leaving the run Running with a Pending/Running decision row the
///       frozen-replay path (PR-1) can finish deterministically. This sweep runs BEFORE the abandoned-Running
///       failure sweep so it RE-DISPATCHES the run (CAS Running→Pending) instead of failing it; the engine
///       re-walk rehydrates the in-flight decision and replays it exactly-once. BOUNDED: a deterministically
///       crashing run is recovered at most <see cref="StuckRunReconcilerService.MaxSupervisorRunRecoveries"/>
///       times (counted from durable <c>supervisor.run_recovered</c> ledger records), after which it falls
///       through to the abandoned-Running failure sweep and terminates cleanly — never an infinite loop.
///       Flag-OFF byte-identical (no extra query when the lane is off).</item>
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
    public int RedispatchedFromStrandedSuspended { get; init; }

    /// <summary>Supervisor self-advances re-fired because the post-commit ResumeWaitAsync enqueue was lost (PR-E E2). 0 when the supervisor lane is off.</summary>
    public int RecoveredSupervisorAdvance { get; init; }

    /// <summary>Abandoned-Running supervisor runs with a recoverable in-flight decision that were re-dispatched instead of failed (PR-E P1-2). 0 when the supervisor lane is off.</summary>
    public int RecoveredAbandonedSupervisorRun { get; init; }

    /// <summary>Active-rerun leases freed because their fork reached a terminal state (the backstop for the inline release the engine does on completion).</summary>
    public int ReleasedRerunLeases { get; init; }

    public int Total => RedispatchedFromPending + RevertedFromEnqueued + MarkedAbandonedFromRunning + RedispatchedFromStrandedSuspended + RecoveredSupervisorAdvance + RecoveredAbandonedSupervisorRun + ReleasedRerunLeases;
}
