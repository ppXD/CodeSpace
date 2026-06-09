using CodeSpace.Messages.Enums;

namespace CodeSpace.Core.Services.Agents;

/// <summary>
/// The AgentRun lifecycle as a pure state machine: Queued → Running → terminal
/// (Succeeded | Failed | Cancelled | TimedOut). The single source of truth for which status
/// transitions are legal, so every writer (AgentRunService, a future reconciler, the agent.code node)
/// agrees on the rules. Pure + side-effect-free → unit-tested exhaustively.
/// </summary>
public static class AgentRunStateMachine
{
    /// <summary>True when <paramref name="status"/> is a terminal state (no further transition out of it).</summary>
    public static bool IsTerminal(AgentRunStatus status) =>
        status is AgentRunStatus.Succeeded or AgentRunStatus.Failed or AgentRunStatus.Cancelled or AgentRunStatus.TimedOut;

    /// <summary>
    /// True when moving <paramref name="from"/> → <paramref name="to"/> is allowed: <c>Running</c> only
    /// from <c>Queued</c>; <c>Succeeded</c> only from <c>Running</c> (a run can't succeed without
    /// running); the other terminals from <c>Queued</c> or <c>Running</c> (a run can fail / be cancelled
    /// / time out before it ever starts). Nothing transitions out of a terminal, and <c>Queued</c> is
    /// never a target.
    /// </summary>
    public static bool IsLegalTransition(AgentRunStatus from, AgentRunStatus to) => to switch
    {
        AgentRunStatus.Running => from == AgentRunStatus.Queued,
        AgentRunStatus.Succeeded => from == AgentRunStatus.Running,
        AgentRunStatus.Failed or AgentRunStatus.Cancelled or AgentRunStatus.TimedOut => from is AgentRunStatus.Queued or AgentRunStatus.Running,
        _ => false,
    };
}
