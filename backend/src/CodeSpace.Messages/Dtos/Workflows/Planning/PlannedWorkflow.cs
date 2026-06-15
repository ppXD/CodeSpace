namespace CodeSpace.Messages.Dtos.Workflows.Planning;

/// <summary>
/// The planner's structured output — a task broken into ordered subtasks plus the framing a reviewer
/// needs to judge the plan (success criteria, risks, the recommended execution shape). It is a DATA noun
/// (Rule 18.1): the LLM produces it constrained by a JSON Schema (the planner's response contract), and a
/// projector deterministically maps it into a FIXED workflow skeleton — the model never names node types
/// or wiring directly. Until a human saves+runs the projected definition through the existing pipeline,
/// nothing here can execute.
///
/// <para>The schema this round-trips from lives next to the planner as <c>PlannerSchema.ResponseSchema</c>
/// (the commit-contract, pinned by a unit test). Field names here MUST match that schema's property names
/// so a schema-valid object deserializes cleanly.</para>
/// </summary>
public sealed record PlannedWorkflow
{
    /// <summary>The restated top-level goal the plan addresses (the planner's understanding of the task).</summary>
    public required string Goal { get; init; }

    /// <summary>Ordered subtasks the goal decomposes into — the collection a downstream <c>flow.map</c> fans out over.</summary>
    public required IReadOnlyList<PlannedSubtask> Subtasks { get; init; }

    /// <summary>Observable conditions that, together, mean the goal is done — what a reviewer/operator checks.</summary>
    public IReadOnlyList<string> SuccessCriteria { get; init; } = Array.Empty<string>();

    /// <summary>Risks / unknowns the plan carries — surfaced so the human reviewer can weigh them before approving.</summary>
    public IReadOnlyList<string> Risks { get; init; } = Array.Empty<string>();

    /// <summary>
    /// The execution shape the planner recommends for each subtask branch. <c>"coding"</c> projects each
    /// branch onto an <c>agent.code</c> body node; anything else (the default) projects onto a plain
    /// <c>llm.complete</c> body node. The projector switches on this — the model never names a node type.
    /// </summary>
    public string RecommendedWorkflowKind { get; init; } = "analysis";
}

/// <summary>One unit of work in a <see cref="PlannedWorkflow"/>. A data noun (Rule 18.1).</summary>
public sealed record PlannedSubtask
{
    /// <summary>Stable, plan-local id (the planner assigns it; the projector does not depend on its format).</summary>
    public required string Id { get; init; }

    /// <summary>Short human title for the subtask (shown to the reviewer; carried into the branch body as <c>{{item.title}}</c>).</summary>
    public required string Title { get; init; }

    /// <summary>The concrete instruction the branch executes — carried into the body node as <c>{{item.instruction}}</c>.</summary>
    public required string Instruction { get; init; }

    /// <summary>Optional one-line "why this subtask" — helps the reviewer; not required for execution.</summary>
    public string? Rationale { get; init; }
}
