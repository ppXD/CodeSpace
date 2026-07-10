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

    /// <summary>
    /// DC-1 — the model's OWN proposed delivery contract (e.g. "open a pull request against main"), clamped
    /// against the operator's pre-declared <c>SupervisorTurnContext.DeliverySpec</c> at plan-persist time
    /// (<c>SupervisorDeliveryClamp</c>, Core — Messages can't reference it, Rule 18.1) before this payload is
    /// frozen — so what round-trips here is always the EFFECTIVE contract, never the model's raw unclamped
    /// proposal. Null-omitted, so a plan
    /// that names no delivery preference at all serializes byte-identical to before DC-1 (idempotency-key bytes
    /// unchanged).
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DeliverySpec? Delivery { get; init; }
}

/// <summary>One planned subtask the supervisor can later spawn / retry by <see cref="Id"/>.</summary>
public sealed record SupervisorPlannedSubtask
{
    public required string Id { get; init; }

    public required string Title { get; init; }

    public required string Instruction { get; init; }

    /// <summary>
    /// Optional plan-local subtask ids this subtask DEPENDS ON — the build-graph edges (loopability slice 1). The model
    /// authors them so the server can later validate the fan-out as a DAG (slice 2) and order it. Null-omitted
    /// (<c>[JsonIgnore(WhenWritingNull)]</c>) so a subtask with no dependencies serializes byte-identical to before — the
    /// idempotency-key bytes are unchanged. PURE DATA here: recorded + projected; the validator + ordering consumer are follow-ups.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? DependsOn { get; init; }

    /// <summary>
    /// Optional model-authored OBJECTIVE per-subtask acceptance — this unit's "definition of done", reusing the same
    /// noun as a stop / phase (<see cref="SupervisorAcceptanceSpec"/>). Null-omitted (<c>[JsonIgnore(WhenWritingNull)]</c>)
    /// so a subtask without a contract serializes byte-identical to before. PURE DATA here: recorded + projected; the
    /// per-unit acceptance GATE (grade each settled unit against this at the spawn fold) is a follow-up (slice 3).
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public SupervisorAcceptanceSpec? Acceptance { get; init; }

    /// <summary>
    /// S2 — whether this unit is expected to produce a code diff/branch at all. A subtask can legitimately carry an
    /// <see cref="Acceptance"/> contract that verifies something OTHER than a diff (e.g. "the investigation report
    /// names the root cause") without ever being expected to push a branch; without this signal the per-unit fold
    /// unconditionally fails such a unit closed as "no-branch-or-repo" the moment it produces none. Model-declared;
    /// null (the model didn't say) falls back to a server inference off the instruction's leading verb
    /// (<c>SupervisorSubtaskExpectations</c>, Core) — a small, defensive heuristic, never authoritative over an
    /// explicit declaration. Null-omitted (<c>[JsonIgnore(WhenWritingNull)]</c>) so a subtask that doesn't address it
    /// serializes byte-identical to before.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? ExpectsChanges { get; init; }
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

/// <summary>The <c>retry</c> payload: ONE prior subtask id re-run as a fresh agent attempt, optionally with a revised instruction. The decision's rationale (why + evidence) is a decision-level annotation on the frozen payload's root (see <see cref="SupervisorRationale"/>), NOT a retry-specific field.</summary>
public sealed record SupervisorRetryPayload
{
    public required string SubtaskId { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? RevisedInstruction { get; init; }
}

/// <summary>
/// A model-authored, STRUCTURED decision rationale — bounded fields, not raw chain-of-thought. The DECISION's "why",
/// frozen at the ROOT of every verb's canonical payload (the projector injects it uniformly), so the trace can explain
/// WHY the supervisor decided as it did — for a plan, a spawn, a retry, a merge, a stop, alike. Both fields optional;
/// an omitted rationale means the model gave none. Read back generically via <c>SupervisorOutcome.ReadRationale</c>.
/// </summary>
public sealed record SupervisorRationale
{
    /// <summary>Why the supervisor made this decision — the reasoning, one or two sentences.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Why { get; init; }

    /// <summary>The concrete evidence it acted on — the prior error / output / status that drove the decision.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Evidence { get; init; }
}

/// <summary>The <c>merge</c> payload: an optional instruction guiding the synthesis of ALL prior agent results. (A selective subtask subset is NOT honored today — it returns with the richer LLM-synthesis merge slice, see <c>RealSupervisorActionExecutor.Merge.cs</c>.)</summary>
public sealed record SupervisorMergePayload
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SynthesisInstruction { get; init; }

    /// <summary>
    /// I3 (publish-or-park): set ONLY by <c>SupervisorPublishGate</c>'s server-authored substitution of a <c>stop</c>
    /// that would otherwise terminalize accepted-but-unpublished work — never model-authored. Forces integration to
    /// run regardless of the operator's on-disk-integration opt-in (I3 is a correctness floor, not a feature the
    /// operator can leave off) and skips the LLM synthesis facet (the model didn't ask for a combined narrative,
    /// only the server is trying to publish). Null-omitted so a model's own merge serializes byte-identical to before.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? ForcedByPublishGate { get; init; }
}

/// <summary>
/// The <c>publish</c> payload (DC-2b) — SERVER-AUTHORED ONLY, never model-authored (mirrors <see cref="SupervisorMergePayload.ForcedByPublishGate"/>'s "server substitution" shape).
/// </summary>
public sealed record SupervisorPublishPayload
{
    /// <summary>
    /// The REJECTED stop's own model-authored summary, carried forward from <see cref="Supervisor.SupervisorDeliveryGate"/>'s
    /// substitution. <c>SupervisorTurnService.ApplyPostDecisionGate</c> returns this <c>publish</c> decision IN
    /// PLACE of that stop, so the stop's payload never reaches the durable tape — without this field, the PR
    /// title/body deriver could never recover the model's own account of the work on the (common) FIRST forced
    /// publish, since <c>context.PriorDecisions</c> at execution time is this turn's REHYDRATED-BEFORE-TURN tape,
    /// which by construction cannot contain a decision this SAME turn is still deciding. Null-omitted when the
    /// rejected stop carried none (the title/body deriver then falls back to scanning an OLDER persisted stop —
    /// e.g. Room's own post-terminal call, which always reads the run's TRUE final stop).
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? StopSummary { get; init; }
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

    /// <summary>
    /// Whether a stop's <see cref="Outcome"/> label means the run finished WELL (a genuine task success) vs a
    /// graceful-failure / abandoned stop (no-decision · no-model · unknown-decision · anything non-success). Case-insensitive;
    /// the SINGLE shared success-word set producer + consumer agree on — the decision-eval scorecard AND the room's degraded
    /// RESULT render both key on THIS, so a future non-success outcome (a new fail-closed stop) can't silently read as success.
    /// A blank / unknown label is NOT success. Generic: never special-case the known degraded strings.
    /// </summary>
    public static bool IsSuccessOutcome(string? outcome) =>
        outcome?.Trim().ToLowerInvariant() is "completed" or "complete" or "success" or "succeeded" or "done" or "ok";

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
