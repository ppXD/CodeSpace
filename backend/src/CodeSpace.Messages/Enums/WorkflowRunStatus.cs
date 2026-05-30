namespace CodeSpace.Messages.Enums;

/// <summary>
/// Run-level lifecycle. Mirrors NodeStatus' coarse shape but is independent — a Failure on
/// any node sets the run to <see cref="Failure"/>; a successful Terminal sets it to
/// <see cref="Success"/>. <see cref="Cancelled"/> is operator-initiated mid-flight.
///
/// <para>State transitions are guarded by atomic CAS (compare-and-swap) UPDATEs in
/// <c>IWorkflowRunDispatcher</c> + the engine entry. Two workers trying to claim the same
/// run cannot both succeed: the SQL <c>UPDATE ... WHERE Status = X</c> is the
/// single-writer guarantee. <see cref="Pending"/> → <see cref="Enqueued"/> →
/// <see cref="Running"/> → terminal; any uncaught failure pre-engine reverts to
/// <see cref="Pending"/> so the stuck-run reconciler can retry.</para>
/// </summary>
public enum WorkflowRunStatus
{
    /// <summary>Created, awaiting dispatch. The stuck-run reconciler retries rows stuck here past a threshold.</summary>
    Pending,

    /// <summary>
    /// Dispatcher transitioned this row to <c>Enqueued</c> via atomic CAS and handed it
    /// to the background-job client. Engine will pick up + transition to <see cref="Running"/>
    /// via another atomic CAS. If the worker dies before that pickup, the reconciler finds
    /// rows stuck here past a longer threshold and re-dispatches.
    /// </summary>
    Enqueued,

    /// <summary>Engine actively walking the DAG. Atomic claim from <see cref="Enqueued"/>.</summary>
    Running,

    /// <summary>A Terminal node returned Success and the engine wrapped up cleanly.</summary>
    Success,

    /// <summary>Any node returned Failure, OR engine itself crashed (transitioned via watchdog).</summary>
    Failure,

    /// <summary>Operator hit "Cancel" via the UI; engine stopped at the next safe point.</summary>
    Cancelled,

    /// <summary>
    /// A node returned <c>Suspended</c> — the run is intentionally PAUSED, waiting on an external
    /// signal (a timer wake, a human approval, or an external callback). NOT terminal and NOT
    /// "stuck": the stuck-run reconciler scans only Pending/Enqueued/Running, so a Suspended run
    /// survives its sweeps untouched. A resume signal flips it back to <see cref="Pending"/> and
    /// re-dispatches; the durable walker rehydrates and continues from where it paused.
    /// </summary>
    Suspended
}
