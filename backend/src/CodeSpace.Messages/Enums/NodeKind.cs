namespace CodeSpace.Messages.Enums;

/// <summary>
/// What role a node plays in the graph. Drives the engine's start/terminate logic AND the
/// frontend palette grouping (triggers go in the "When" tray, terminals in the "End" tray,
/// everything else in the "Steps" tray).
///
/// Kept intentionally tiny — three categories. New node TYPES (git.fetch_pr_diff, llm.complete,
/// http.post, …) do NOT need new NodeKind values; they are all <see cref="Regular"/>. New
/// Kind values would mean "the engine itself has a new lifecycle phase" which is rare enough
/// to be worth a migration.
/// </summary>
public enum NodeKind
{
    /// <summary>Step in the middle of the graph. Has inputs + outputs. Most nodes.</summary>
    Regular,

    /// <summary>
    /// Entry point. Has outputs only (no inputs). Exactly one of these per workflow definition;
    /// validator enforces. Trigger-payload data is exposed as the trigger's outputs so downstream
    /// nodes read it via the same {{ref}} mechanism as any other inter-node data flow.
    /// </summary>
    Trigger,

    /// <summary>
    /// Exit point. Has inputs only (no outputs). A workflow MAY have multiple terminals — the
    /// engine stops the run when any one of them succeeds.
    /// </summary>
    Terminal
}
