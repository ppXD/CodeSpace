using System.Text.Json.Serialization;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Dtos.Workflows.Planning;

namespace CodeSpace.Messages.Plans;

/// <summary>
/// One unit of work inside a persisted <c>work_plan</c> version — the SINGLE item vocabulary every plan
/// producer maps into (Rule 18.1 data noun). Two producers exist today: the <c>plan.author</c> graph node
/// (items from <c>PlannedSubtask</c>) and the supervisor's <c>plan</c> decision (items from
/// <c>SupervisorPlannedSubtask</c>); both write the same shape so every reader (confirmation card, run-detail
/// checklist, acceptance gates) is producer-agnostic.
///
/// <para>The item carries the CONTRACT only — what to do, what it depends on, and how completion is
/// objectively verified (<see cref="Acceptance"/>). Execution state (which agent ran it, whether acceptance
/// passed) is NOT stored here: it stays on the already-durable tape (agent runs + per-unit verdict folds)
/// and is joined at read time, so there is exactly one source of truth and replay stays deterministic.</para>
///
/// <para>Serialized into <c>work_plan.items_json</c> with <c>AgentJson.Options</c> (camelCase, string enums);
/// every optional field is null-omitted so a minimal item stays byte-stable as the contract grows.</para>
/// </summary>
public sealed record WorkPlanItem
{
    /// <summary>Stable, plan-local id (the producer assigns it; spawn/retry/acceptance folds reference it).</summary>
    public required string Id { get; init; }

    /// <summary>Short human title — the checklist line the operator reads.</summary>
    public required string Title { get; init; }

    /// <summary>The concrete instruction the executing agent receives.</summary>
    public required string Instruction { get; init; }

    /// <summary>Optional one-line "why this item" — for the plan reviewer; never executed.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Rationale { get; init; }

    /// <summary>Optional plan-local item ids this item depends on — the plan's DAG edges (absent → independent).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? DependsOn { get; init; }

    /// <summary>
    /// Optional OBJECTIVE per-item acceptance — this unit's definition of done, reusing the same noun the
    /// supervisor's per-unit gate already grades (<see cref="SupervisorAcceptanceSpec"/>): a command argv
    /// (TestsPass) or deliverable paths (ArtifactPresent). Authored WITH the task (the plan is where the
    /// oracle is written), graded by the Evaluation layer later.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public SupervisorAcceptanceSpec? Acceptance { get; init; }

    /// <summary>Optional producer-picked harness for this item (e.g. "claude-code"); null → the run default.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Harness { get; init; }

    /// <summary>Optional producer-picked model id for this item; null → the harness default.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Model { get; init; }

    /// <summary>The graph-tier mapping: a <c>plan.author</c> planner subtask → the unified item shape.</summary>
    public static WorkPlanItem From(PlannedSubtask subtask) => new()
    {
        Id = subtask.Id,
        Title = subtask.Title,
        Instruction = subtask.Instruction,
        Rationale = subtask.Rationale,
        DependsOn = subtask.DependsOn,
        Acceptance = subtask.Acceptance,
        Harness = subtask.Harness,
        Model = subtask.Model,
    };

    /// <summary>The loop-tier mapping: a supervisor plan-decision subtask → the unified item shape.</summary>
    public static WorkPlanItem From(SupervisorPlannedSubtask subtask) => new()
    {
        Id = subtask.Id,
        Title = subtask.Title,
        Instruction = subtask.Instruction,
        DependsOn = subtask.DependsOn,
        Acceptance = subtask.Acceptance,
    };
}
