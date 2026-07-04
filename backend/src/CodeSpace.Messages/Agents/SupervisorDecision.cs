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

    /// <summary>True when this decision ends the turn loop (<see cref="SupervisorDecisionKinds.Stop"/>). The run then completes via the normal walk.</summary>
    public bool IsTerminal => Kind == SupervisorDecisionKinds.Stop;
}

/// <summary>The model call that authored a supervisor decision — the model id + its token usage, captured off the decider's LLM response. A data noun (Rule 18.1) folded into the decision's outcome so a read can attribute the decision.</summary>
public sealed record SupervisorModelUsage
{
    public required string Model { get; init; }

    public int? InputTokens { get; init; }

    public int? OutputTokens { get; init; }
}
