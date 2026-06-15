namespace CodeSpace.Messages.Agents;

/// <summary>
/// What one supervisor turn resolved to (a data noun, Rule 18.1) — the turn service's instruction to the
/// node. THREE shapes (the dual resume path, PR-E E3):
/// <list type="bullet">
///   <item>FINISH — a terminal <c>stop</c> / budget-exhausted decision reached its outcome; the node returns
///         a terminal node result + the run completes via the normal walk.</item>
///   <item>SELF-ADVANCE PARK — a SYNCHRONOUS non-terminal decision (plan / merge) settled in-process; the node
///         suspends on a <c>SupervisorDecision</c> wait under the next-turn IterationKey, self-advancing into
///         the next turn (the E2 path).</item>
///   <item>AGENT-WAIT PARK — an ASYNC decision (spawn / retry) staged K real <c>AgentRun</c> waits; the node
///         suspends on THOSE (no self-advance) and the wait-for-all barrier resumes the supervisor once all K
///         agents complete (<see cref="ParkedAgentWaitCount"/> &gt; 0).</item>
/// </list>
/// </summary>
public sealed record SupervisorTurnResult
{
    /// <summary>True when the loop is done (a terminal <c>stop</c>). The node returns success; false → the node parks for the next turn.</summary>
    public required bool IsFinished { get; init; }

    /// <summary>The decision kind this turn settled (audit/output surface).</summary>
    public required string DecisionKind { get; init; }

    /// <summary>The terminal reason on finish (e.g. the stop reason, or "budget exhausted"). Null while parking.</summary>
    public string? TerminalReason { get; init; }

    /// <summary>The folded context for the NEXT turn — carried as the self-advance park wait's payload. Null on finish.</summary>
    public SupervisorTurnContext? NextTurn { get; init; }

    /// <summary>
    /// &gt; 0 when this turn's decision staged that many real <c>AgentRun</c> waits (spawn / retry) — the node
    /// parks on THEM, NOT on a self-advance wait; the wait-for-all barrier drives the next turn once all
    /// complete. 0 for a synchronous self-advance (plan / merge) or a finish.
    /// </summary>
    public int ParkedAgentWaitCount { get; init; }

    /// <summary>True when this turn parked on staged agent waits (async) rather than a self-advance wait (synchronous). The node reads this to choose its suspend path.</summary>
    public bool ParkedOnAgentWaits => !IsFinished && ParkedAgentWaitCount > 0;

    public static SupervisorTurnResult Finished(string decisionKind, string? terminalReason) =>
        new() { IsFinished = true, DecisionKind = decisionKind, TerminalReason = terminalReason };

    /// <summary>A SYNCHRONOUS non-terminal decision (plan / merge) — the node self-advances on a SupervisorDecision wait carrying the next-turn context.</summary>
    public static SupervisorTurnResult SelfAdvance(string decisionKind, SupervisorTurnContext nextTurn) =>
        new() { IsFinished = false, DecisionKind = decisionKind, NextTurn = nextTurn, ParkedAgentWaitCount = 0 };

    /// <summary>An ASYNC decision (spawn / retry) — the executor staged <paramref name="agentWaitCount"/> AgentRun waits; the node parks on them (the barrier resumes it).</summary>
    public static SupervisorTurnResult ParkOnAgents(string decisionKind, SupervisorTurnContext nextTurn, int agentWaitCount) =>
        new() { IsFinished = false, DecisionKind = decisionKind, NextTurn = nextTurn, ParkedAgentWaitCount = agentWaitCount };
}
