namespace CodeSpace.Messages.Dtos.Workflows.Planning;

/// <summary>
/// The coordinator's per-ROUND decision in an L3 checkpoint-coordinated plan — the commit-contract the
/// model emits at the end of each round to decide what the loop does next. A DATA noun (Rule 18.1): the
/// coordinator <c>llm.complete</c> node is constrained by <c>CoordinatorSchema.ResponseSchema</c> to emit a
/// schema-valid object on its <c>json</c> output, and the enclosing <c>flow.loop</c> reads two fields off it
/// — <see cref="Decision"/> drives termination (<c>done</c>/<c>abort</c> stop the loop) and
/// <see cref="ReworkSubtasks"/> re-seeds the next round's <c>flow.map</c> fan-out.
///
/// <para>The schema this round-trips from lives next to the planner as
/// <c>CoordinatorSchema.ResponseSchema</c> (pinned by a unit test). Field names here MUST match that
/// schema's property names so a schema-valid object deserializes cleanly. This is consumed by the engine
/// through <c>{{...}}</c> refs on the loop config — nothing here runs by itself.</para>
/// </summary>
public sealed record CoordinatorDecision
{
    /// <summary>What the round resolved to. <c>done</c> / <c>abort</c> terminate the loop; <c>rework</c> runs another round over <see cref="ReworkSubtasks"/>; <c>ask_human</c> is reserved for a future in-loop HITL pause (see CoordinatorSchema follow-up note).</summary>
    public required string Decision { get; init; }

    /// <summary>One-paragraph summary of the round's results + the reasoning behind the decision — what a reviewer reads.</summary>
    public string Summary { get; init; } = "";

    /// <summary>The next round's work when <see cref="Decision"/> is <c>rework</c> — same {id,title,instruction} shape as a <see cref="PlannedSubtask"/>; the loop's <c>subtasks</c> update re-seeds the map from this.</summary>
    public IReadOnlyList<PlannedSubtask> ReworkSubtasks { get; init; } = Array.Empty<PlannedSubtask>();

    /// <summary>The question to a human when <see cref="Decision"/> is <c>ask_human</c>. Empty otherwise.</summary>
    public string Question { get; init; } = "";

    /// <summary>The coordinator's risk read of its own decision — surfaced for the reviewer; not consumed by the loop wiring.</summary>
    public string RiskLevel { get; init; } = "low";
}
