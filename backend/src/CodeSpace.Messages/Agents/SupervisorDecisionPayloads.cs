using System.Text.Json.Serialization;

namespace CodeSpace.Messages.Agents;

/// <summary>
/// The per-verb canonical payloads a supervisor decision freezes into its ledger row (PR-E E3 — data nouns,
/// Rule 18.1). Each is the deterministic JSON the decider emits + the turn loop hashes into the server-derived
/// idempotency key, so the SAME decision at the SAME turn produces the SAME bytes → the same key → exactly-once
/// on replay. The decider canonicalizes via <c>AgentJson.Options</c>; these records are the deserialized view
/// the executor reads to drive the side effect.
///
/// <para>These are server-side projections of the model's <c>SupervisorModelDecision</c> — the model never
/// addresses graph topology (no node id / type key / run id); the server turns a verb + bounded payload into a
/// side effect.</para>
/// </summary>
public sealed record SupervisorPlanPayload
{
    public string Goal { get; init; } = "";

    public IReadOnlyList<SupervisorPlannedSubtask> Subtasks { get; init; } = Array.Empty<SupervisorPlannedSubtask>();

    /// <summary>
    /// Optional model-authored SEMANTIC PHASES (L4 arc C) grouping the <see cref="Subtasks"/> into named, accepting
    /// stages. Null-omitted (<c>[JsonIgnore(WhenWritingNull)]</c>) so a flat-subtask plan serializes byte-identical to
    /// before (the idempotency-key bytes are unchanged). Absent ⇒ the plan is the flat subtask list. See <see cref="SupervisorPlanPhase"/>.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<SupervisorPlanPhase>? Phases { get; init; }
}

/// <summary>One planned subtask the supervisor can later spawn / retry by <see cref="Id"/>.</summary>
public sealed record SupervisorPlannedSubtask
{
    public required string Id { get; init; }

    public required string Title { get; init; }

    public required string Instruction { get; init; }
}

/// <summary>The <c>spawn</c> payload: prior-plan subtask ids to fan out as parallel agent runs (K = the list length), plus an optional per-agent dispatch override per subtask (L4 arc B).</summary>
public sealed record SupervisorSpawnPayload
{
    public IReadOnlyList<string> SubtaskIds { get; init; } = Array.Empty<string>();

    /// <summary>
    /// Optional model-authored per-agent dispatch specs (L4 arc B) — one <see cref="SupervisorAgentDispatch"/> per
    /// subtask the model wants to give a distinct role / repo subset / execution envelope, keyed by
    /// <see cref="SupervisorAgentDispatch.SubtaskId"/>. Null-omitted (<c>[JsonIgnore(WhenWritingNull)]</c>) so a spawn
    /// WITHOUT per-agent specs serializes byte-identical to the plain <see cref="SubtaskIds"/> fan-out — the
    /// idempotency-key bytes are unchanged. Absent ⇒ every staged agent inherits the run-level profile (today's behaviour).
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<SupervisorAgentDispatch>? Agents { get; init; }
}

/// <summary>The <c>retry</c> payload: ONE prior subtask id re-run as a fresh agent attempt, optionally with a revised instruction.</summary>
public sealed record SupervisorRetryPayload
{
    public required string SubtaskId { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? RevisedInstruction { get; init; }
}

/// <summary>The <c>merge</c> payload: an optional instruction guiding the synthesis of ALL prior agent results. (A selective subtask subset is NOT honored today — it returns with the richer LLM-synthesis merge slice, see <c>RealSupervisorActionExecutor.Merge.cs</c>.)</summary>
public sealed record SupervisorMergePayload
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SynthesisInstruction { get; init; }
}

/// <summary>
/// The <c>resolve</c> payload (resolver loop #379): the decider only CHOOSES to attempt resolution — the resolver
/// task's content (which branches, which conflicted files, the build/test instruction) is assembled DETERMINISTICALLY
/// by <c>RealSupervisorActionExecutor.Resolve</c> from the durable conflicted-merge + spawn outcomes, never by the
/// model. The single optional <see cref="Note"/> lets the decider record WHY it chose to attempt (audit only — it
/// never steers the resolution), so the payload carries no branch/file fields the model could get wrong.
/// </summary>
public sealed record SupervisorResolvePayload
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Note { get; init; }
}

/// <summary>The <c>ask_human</c> payload: the question to ask (parked for E4).</summary>
public sealed record SupervisorAskHumanPayload
{
    public required string Question { get; init; }
}

/// <summary>The <c>stop</c> payload: the terminal outcome label + a short summary, plus an optional model-authored acceptance check.</summary>
public sealed record SupervisorStopPayload
{
    /// <summary>The fail-closed NON-CONFORMANT outcome label: the decider stamps this <see cref="Outcome"/> when the model produced no usable decision (a degraded / capability-miss reply) and it degraded to a clean stop rather than crashing. The shared signature a consumer (the decision-eval) keys on to reject a non-conformant reply that must NEVER score as a genuine 'stop' decision — pinned here so producer + consumer can't drift.</summary>
    public const string NonConformantOutcome = "no-decision";

    public required string Outcome { get; init; }

    public string Summary { get; init; } = "";

    /// <summary>
    /// Optional model-authored OBJECTIVE acceptance for the terminal result — the L3→L4 "definition of done": a
    /// server-run check the supervisor declares so "done" is a verified fact, not a self-report. Null-omitted
    /// (<c>[JsonIgnore(WhenWritingNull)]</c>) so a stop WITHOUT acceptance serializes byte-identical to before —
    /// the idempotency-key bytes are unchanged and exactly-once replay is unaffected. See <see cref="SupervisorAcceptanceSpec"/>.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public SupervisorAcceptanceSpec? Acceptance { get; init; }
}
