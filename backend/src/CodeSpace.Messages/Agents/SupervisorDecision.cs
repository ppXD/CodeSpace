namespace CodeSpace.Messages.Agents;

/// <summary>
/// The next decision a <c>ISupervisorDecider</c> emits given a <see cref="SupervisorTurnContext"/> — the
/// kind (a <see cref="SupervisorDecisionKinds"/> verb) plus its canonical payload JSON (a data noun, Rule
/// 18.1). The payload is what gets FROZEN into the ledger row + hashed into the server-derived idempotency
/// key, so the decider canonicalizes it deterministically (same turn + same inputs → same bytes → same key
/// → exactly-once on replay). <see cref="IsTerminal"/> tells the turn loop whether to finish (a
/// <see cref="SupervisorDecisionKinds.Stop"/>) or park + re-enter for the next turn.
/// </summary>
public sealed record SupervisorDecision
{
    /// <summary>The decision verb — a <see cref="SupervisorDecisionKinds"/> value.</summary>
    public required string Kind { get; init; }

    /// <summary>The emitted decision's canonical JSON — frozen into the ledger row + hashed into the idempotency key.</summary>
    public required string PayloadJson { get; init; }

    /// <summary>The model call that AUTHORED this decision (model + tokens) — captured from the decider's LLM response. NOT part of the hashed payload / idempotency key (it never influences the decision's identity); the turn service folds it into the NON-hashed outcome so the journal can attribute how the decision was made. Null for a stub / no-model decision.</summary>
    public SupervisorModelUsage? Usage { get; init; }

    /// <summary>
    /// The MODEL-critic reviews that shaped this decision before it executed (the adversarial middle: a first draft
    /// flagged → revised → re-reviewed) — carried like <see cref="Usage"/>: NOT hashed (never part of the decision's
    /// identity), folded into the NON-hashed outcome by the turn service so the journal can render the draft→verdict→
    /// revision chain instead of leaving the discarded draft an anonymous model call. Empty when no critic ran (the
    /// overwhelmingly common case — byte-identical), and REAL-AGENT verdicts are deliberately NOT carried here (their
    /// reviewer runs are already first-class journal citizens).
    /// </summary>
    public IReadOnlyList<SupervisorDecisionReview> Reviews { get; init; } = Array.Empty<SupervisorDecisionReview>();

    /// <summary>True when this decision ends the turn loop (<see cref="SupervisorDecisionKinds.Stop"/>). The run then completes via the normal walk.</summary>
    public bool IsTerminal => Kind == SupervisorDecisionKinds.Stop;
}

/// <summary>
/// One MODEL-critic verdict on a supervisor decision (a data noun, Rule 18.1) — what the in-process critic concluded
/// about a draft (or the final decision), plus the DISCARDED draft's attribution when a revision followed, so the
/// journal shows the whole exchange: which call authored the draft, why it was flagged, and that the decision the
/// operator sees is the revision.
/// </summary>
public sealed record SupervisorDecisionReview
{
    public required bool Approved { get; init; }

    /// <summary>The critic's one-line rationale — WHY it approved / flagged.</summary>
    public required string Rationale { get; init; }

    /// <summary>Evidence-attached issues, each pre-rendered "text (evidence: …)". Empty on an approval.</summary>
    public IReadOnlyList<string> Issues { get; init; } = Array.Empty<string>();

    /// <summary>WHAT was reviewed — <c>plan</c> (the plan-scoped critic) or <c>decision</c> (the all-step critic).</summary>
    public required string Scope { get; init; }

    /// <summary>The DISCARDED draft this review flagged, when a revision followed — its verb + the model call that authored it, pre-rendered ("plan draft · via metis-coder-max · 8.2k tokens") so the once-anonymous call is attributed. Null when the review approved (nothing was discarded).</summary>
    public string? DraftAttribution { get; init; }
}

/// <summary>The model call that authored a supervisor decision — the model id + its token usage, captured off the decider's LLM response. A data noun (Rule 18.1) folded into the decision's outcome so a read can attribute the decision.</summary>
public sealed record SupervisorModelUsage
{
    public required string Model { get; init; }

    public int? InputTokens { get; init; }

    public int? OutputTokens { get; init; }
}
