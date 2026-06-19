using System.Text.Json.Serialization;

namespace CodeSpace.Messages.Agents;

/// <summary>
/// A model-authored SEMANTIC PHASE of a supervisor plan (the L3→L4 step, arc C) — a data noun (Rule 18.1). When the
/// supervisor model plans, it MAY group the flat subtasks into named phases (e.g. "Investigate", "Implement",
/// "Review"), each with its own acceptance — so the run reads as a coherent plan, not an opaque subtask list. The
/// phases are RECORDED + projected (the supervisor scorecard / tasks-phases surface reads them); enforced
/// phase-gating (block the next phase until this phase's <see cref="Acceptance"/> passes) is a deferred follow-up.
///
/// <para>Carried on the optional <c>plan.phases[]</c>; absent ⇒ the flat subtask plan, byte-identical to before.</para>
/// </summary>
public sealed record SupervisorPlanPhase
{
    /// <summary>Stable, plan-local id for the phase. REQUIRED.</summary>
    public required string Id { get; init; }

    /// <summary>Short human title (e.g. "Investigate", "Implement", "Review"). REQUIRED.</summary>
    public required string Title { get; init; }

    /// <summary>The plan-local subtask ids this phase groups (a subset of the plan's <see cref="SupervisorPlanPayload.Subtasks"/>). Empty for a descriptive-only phase.</summary>
    public IReadOnlyList<string> SubtaskIds { get; init; } = Array.Empty<string>();

    /// <summary>Optional per-phase OBJECTIVE acceptance (reuses the same noun as a stop's acceptance) — the server-runnable check this phase is "done" by. Recorded + projected in v1; the enforcing gate is a follow-up. Null-omitted so a phase without acceptance is byte-stable.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public SupervisorAcceptanceSpec? Acceptance { get; init; }
}
