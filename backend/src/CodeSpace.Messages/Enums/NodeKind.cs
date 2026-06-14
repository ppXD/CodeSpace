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
    Terminal,

    /// <summary>
    /// Container that owns a body subgraph and re-runs it per iteration (e.g. <c>flow.loop</c>).
    /// The engine dispatches this Kind specially — like Trigger/Terminal it marks "a new engine
    /// lifecycle phase" (run the owned body N times, keyed by iteration), not just another step.
    /// Body nodes point back via <c>NodeDefinition.ParentId</c>.
    /// </summary>
    Loop,

    /// <summary>
    /// Scope container that owns a body subgraph and runs it ONCE with a try/catch boundary
    /// (<c>flow.try</c>): an unhandled failure anywhere in the body routes the run down the container's
    /// <c>catch</c> output (the failure becomes data on the handler branch) instead of failing the run.
    /// Engine-dispatched like <see cref="Loop"/>; body nodes point back via <c>NodeDefinition.ParentId</c>.
    /// </summary>
    Try,

    /// <summary>
    /// Fan-out container that owns a body subgraph and runs it ONCE PER ELEMENT of a bound collection
    /// (<c>flow.map</c>): the N element-branches run as a bounded-parallel batch, each branch sees its
    /// element as <c>{{item}}</c> / <c>{{index}}</c>, and the per-element results reduce into a keyed
    /// array a downstream synthesizer reads. Engine-dispatched like <see cref="Loop"/>; body nodes point
    /// back via <c>NodeDefinition.ParentId</c>. The new lifecycle phase vs <see cref="Loop"/>: branches
    /// are concurrent instances of one pass (not N sequential passes), and the output is the reduced array.
    /// </summary>
    Map
}
