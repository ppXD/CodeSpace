namespace CodeSpace.Messages.Agents;

/// <summary>
/// What one supervisor turn resolved to (a data noun, Rule 18.1) — the turn service's instruction to the
/// node. Either the loop FINISHES (a <c>stop</c> / budget-exhausted decision reached its terminal outcome →
/// the node returns a terminal node result and the run completes via the normal walk), or it PARKS (a
/// non-terminal decision settled → the node suspends on a <c>SupervisorDecision</c> wait under the
/// next-turn IterationKey, self-advancing into the next turn).
/// </summary>
public sealed record SupervisorTurnResult
{
    /// <summary>True when the loop is done (a terminal <c>stop</c>). The node returns success; false → the node parks for the next turn.</summary>
    public required bool IsFinished { get; init; }

    /// <summary>The decision kind this turn settled (audit/output surface).</summary>
    public required string DecisionKind { get; init; }

    /// <summary>The terminal reason on finish (e.g. the stop reason, or "budget exhausted"). Null while parking.</summary>
    public string? TerminalReason { get; init; }

    /// <summary>The folded context for the NEXT turn — carried as the park wait's payload. Null on finish.</summary>
    public SupervisorTurnContext? NextTurn { get; init; }

    public static SupervisorTurnResult Finished(string decisionKind, string? terminalReason) =>
        new() { IsFinished = true, DecisionKind = decisionKind, TerminalReason = terminalReason };

    public static SupervisorTurnResult Park(string decisionKind, SupervisorTurnContext nextTurn) =>
        new() { IsFinished = false, DecisionKind = decisionKind, NextTurn = nextTurn };
}
