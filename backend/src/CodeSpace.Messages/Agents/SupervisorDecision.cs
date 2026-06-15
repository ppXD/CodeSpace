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

    /// <summary>True when this decision ends the turn loop (<see cref="SupervisorDecisionKinds.Stop"/>). The run then completes via the normal walk.</summary>
    public bool IsTerminal => Kind == SupervisorDecisionKinds.Stop;
}
