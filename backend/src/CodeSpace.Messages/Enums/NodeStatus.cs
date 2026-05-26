namespace CodeSpace.Messages.Enums;

/// <summary>
/// Lifecycle state of a single node execution within a run. Persisted to
/// workflow_run_node.status. The "happy" terminal states are <see cref="Success"/> and
/// <see cref="Skipped"/> — both let downstream nodes proceed. <see cref="Failure"/> halts
/// the whole run (no per-node error recovery today; a future "on-error edge" feature
/// would change this). <see cref="Suspended"/> pauses the run until external resume.
/// </summary>
public enum NodeStatus
{
    /// <summary>Allocated, not yet started. Engine writes this row when frontiering the node.</summary>
    Pending,

    /// <summary>Handler invoked, awaiting result.</summary>
    Running,

    /// <summary>Handler returned outputs successfully.</summary>
    Success,

    /// <summary>Handler returned an error. Halts the run.</summary>
    Failure,

    /// <summary>
    /// Node deliberately did not execute (e.g. branch-condition evaluated false). Downstream
    /// nodes wait on the OTHER incoming branch; if all incoming branches are Skipped the node
    /// itself becomes Skipped.
    /// </summary>
    Skipped,

    /// <summary>
    /// Node returned a suspension token (waiting on human approval, sleep timer, external
    /// callback). Engine persists the token and idles until <see cref="Suspended"/> is
    /// converted to <see cref="Success"/> via a resume call.
    /// </summary>
    Suspended
}
