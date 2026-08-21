using System.Text.Json.Serialization;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Plans;

namespace CodeSpace.Messages.Dtos.Workflows.Planning;

/// <summary>
/// The planner's structured output — a task broken into ordered subtasks plus the framing a reviewer
/// needs to judge the plan (success criteria, risks, the recommended execution shape). It is a DATA noun
/// (Rule 18.1): the LLM produces it constrained by a JSON Schema (the planner's response contract), and a
/// projector deterministically maps it into a FIXED workflow skeleton — the model never names node types
/// or wiring directly. Until a human saves+runs the projected definition through the existing pipeline,
/// nothing here can execute.
///
/// <para>The model-authored projection this round-trips from lives next to the planner as
/// <c>PlannerSchema.ResponseSchema</c>. <c>PlanningJsonContractTests</c> compares that schema with the actual
/// <c>AgentJson.Options</c> output at every bound nested object. Server-stamped provenance fields on this record,
/// and server/runtime-only fields on shared nested records, are deliberately outside the model schema and pinned as
/// exact exclusions rather than being misreported as 1:1 schema fields.</para>
/// </summary>
public sealed record PlannedWorkflow
{
    /// <summary>The restated top-level goal the plan addresses (the planner's understanding of the task).</summary>
    [JsonPropertyName("goal")]
    public required string Goal { get; init; }

    /// <summary>Ordered subtasks the goal decomposes into — the collection a downstream <c>flow.map</c> fans out over.</summary>
    [JsonPropertyName("subtasks")]
    public required IReadOnlyList<PlannedSubtask> Subtasks { get; init; }

    /// <summary>Observable conditions that, together, mean the goal is done — what a reviewer/operator checks.</summary>
    [JsonPropertyName("successCriteria")]
    public IReadOnlyList<string> SuccessCriteria { get; init; } = Array.Empty<string>();

    /// <summary>Risks / unknowns the plan carries — surfaced so the human reviewer can weigh them before approving.</summary>
    [JsonPropertyName("risks")]
    public IReadOnlyList<string> Risks { get; init; } = Array.Empty<string>();

    /// <summary>
    /// The model that AUTHORED this plan, stamped by the planner from its own resolved pool pick — not requested, not
    /// configured: resolved. Null when the producer did not stamp one (a deterministic fake, an older plan).
    ///
    /// <para>Nothing recorded this before, so no test and no operator could tell which model a plan came from. That is
    /// how a gate advertising "a live model authors the plan" ran for its whole life against an in-process fake: with
    /// no model pinned the planner takes the auto path, which returns the first structured client holding a pool
    /// model, and nothing downstream could contradict it. A resolved-model stamp is the smallest fact that makes the
    /// claim checkable.</para>
    /// </summary>
    [JsonPropertyName("authoredByModel"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? AuthoredByModel { get; init; }

    /// <summary>D2 (cross-run learning): which experiment arm this plan was authored under — <c>injected</c> (lessons in the prompt, ids in <see cref="InjectedLessonIds"/>), <c>withheld</c> (lessons existed, deterministically held back — the control), or <c>none</c> (no current lesson existed). Stamped server-side like <see cref="AuthoredByModel"/>.</summary>
    [JsonPropertyName("lessonArm")]
    public string? LessonArm { get; init; }

    /// <summary>The lessons the prompt actually carried — null unless <see cref="LessonArm"/> is <c>injected</c>.</summary>
    [JsonPropertyName("injectedLessonIds")]
    public IReadOnlyList<Guid>? InjectedLessonIds { get; init; }

    /// <summary>
    /// The execution shape the planner recommends for each subtask branch. <c>"coding"</c> projects each
    /// branch onto an <c>agent.run</c> body node; anything else (the default) projects onto a plain
    /// <c>llm.complete</c> body node. The projector switches on this — the model never names a node type.
    /// </summary>
    [JsonPropertyName("recommendedWorkflowKind")]
    public string RecommendedWorkflowKind { get; init; } = "analysis";

    /// <summary>
    /// The planner's SELF-BYPASS: <c>true</c> means the goal needs no execution at all — the plan itself
    /// answers it (deliberately strict criteria in the prompt; the default <c>false</c> keeps every prior
    /// plan's behaviour). Surfaced by the <c>plan.author</c> node as <c>executionNeeded = !hasEnoughContext</c>
    /// so a downstream <c>logic.if</c> can route straight to synthesis.
    /// </summary>
    [JsonPropertyName("hasEnoughContext")]
    public bool HasEnoughContext { get; init; }

    /// <summary>Optional defaults the planner chose where the goal was ambiguous — recorded so the reviewer sees what was assumed rather than re-deriving it. Null-omitted.</summary>
    [JsonPropertyName("assumptions"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? Assumptions { get; init; }

    /// <summary>Optional operator questions (each with 2-4 options + a recommended default) — the plan-confirmation form's fodder. Null-omitted; absent ⇒ the plan needs no operator input.</summary>
    [JsonPropertyName("questions"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<WorkPlanQuestion>? Questions { get; init; }
}

/// <summary>One unit of work in a <see cref="PlannedWorkflow"/>. A data noun (Rule 18.1).</summary>
public sealed record PlannedSubtask
{
    /// <summary>Stable, plan-local id (the planner assigns it; the projector does not depend on its format).</summary>
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    /// <summary>Short human title for the subtask (shown to the reviewer; carried into the branch body as <c>{{item.title}}</c>).</summary>
    [JsonPropertyName("title")]
    public required string Title { get; init; }

    /// <summary>The concrete instruction the branch executes — carried into the body node as <c>{{item.instruction}}</c>.</summary>
    [JsonPropertyName("instruction")]
    public required string Instruction { get; init; }

    /// <summary>Optional one-line "why this subtask" — helps the reviewer; not required for execution.</summary>
    [JsonPropertyName("rationale")]
    public string? Rationale { get; init; }

    /// <summary>P2 allocation — the harness the planner picked for THIS subtask from the capability catalog (e.g. "claude-code"). Carried into the branch body as <c>{{item.harness}}</c>. Null → the projector fills the default so every branch has a valid harness.</summary>
    [JsonPropertyName("harness")]
    public string? Harness { get; init; }

    /// <summary>P2 allocation — the model the planner picked for THIS subtask from the run's pool. Carried into the branch body as <c>{{item.model}}</c>. Null → the harness default. The catalog steers the planner to a model whose provider the chosen harness can drive.</summary>
    [JsonPropertyName("model")]
    public string? Model { get; init; }

    /// <summary>Optional OPEN subtask kind (e.g. "research" / "code" / "analysis" / "write") — an allocation + rendering hint, never a dispatch switch. Null-omitted.</summary>
    [JsonPropertyName("kind"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Kind { get; init; }

    /// <summary>
    /// Optional plan-local subtask ids this subtask DEPENDS ON — the plan's DAG edges, the same vocabulary the
    /// supervisor's planned subtasks carry. Null-omitted so a dependency-free subtask serializes byte-identical.
    /// </summary>
    [JsonPropertyName("dependsOn"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? DependsOn { get; init; }

    /// <summary>
    /// Optional OBJECTIVE per-subtask acceptance — the unit's definition of done, authored WITH the task (the
    /// sprint-contract move: the plan is where the oracle is written). Reuses the supervisor's acceptance noun
    /// (<see cref="SupervisorAcceptanceSpec"/>: a TestsPass argv or ArtifactPresent deliverable paths) so both
    /// plan producers speak one acceptance vocabulary. Null-omitted — byte-identical when unauthored.
    /// </summary>
    [JsonPropertyName("acceptance"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public SupervisorAcceptanceSpec? Acceptance { get; init; }

    /// <summary>Optional SUBJECTIVE per-subtask acceptance criteria — short free-text qualities a reviewer/critic grades (never executed; the objective half is <see cref="Acceptance"/>). Null-omitted.</summary>
    [JsonPropertyName("acceptanceCriteria"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? AcceptanceCriteria { get; init; }
}
