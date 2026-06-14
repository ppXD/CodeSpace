using CodeSpace.Messages.Agents;

namespace CodeSpace.Core.Services.Agents;

/// <summary>
/// The <c>ToolCallLedger</c> lifecycle as a pure state machine: <c>Pending</c> → terminal
/// (<c>Succeeded</c> | <c>Failed</c> | <c>Denied</c>) for a synchronously-resolved call, or
/// <c>Pending</c> → <c>AwaitingApproval</c> → (<c>Succeeded</c> | <c>Failed</c> | <c>Expired</c>) for a
/// durable mid-turn approval (item D). The single source of truth for which transitions are legal, so the
/// service's status-guarded CAS can REJECT an illegal/lost transition rather than silently apply it. Mirrors
/// <see cref="AgentRunStateMachine"/>: pure + side-effect-free → unit-tested exhaustively.
/// </summary>
public static class ToolCallLedgerStateMachine
{
    /// <summary>True when <paramref name="status"/> is terminal (no further transition out of it).</summary>
    public static bool IsTerminal(ToolCallLedgerStatus status) =>
        status is ToolCallLedgerStatus.Succeeded or ToolCallLedgerStatus.Failed or ToolCallLedgerStatus.Denied or ToolCallLedgerStatus.Expired;

    /// <summary>
    /// True when moving <paramref name="from"/> → <paramref name="to"/> is allowed: a call may succeed / fail /
    /// be denied / pause for approval out of <c>Pending</c>; out of <c>AwaitingApproval</c> it may succeed / fail
    /// / expire (a denial is a synchronous-only verdict, an approval can never re-pend). Nothing transitions out
    /// of a terminal, and <c>Pending</c> is never a target.
    /// </summary>
    public static bool IsLegalTransition(ToolCallLedgerStatus from, ToolCallLedgerStatus to) => to switch
    {
        ToolCallLedgerStatus.Succeeded => from is ToolCallLedgerStatus.Pending or ToolCallLedgerStatus.AwaitingApproval,
        ToolCallLedgerStatus.Failed => from is ToolCallLedgerStatus.Pending or ToolCallLedgerStatus.AwaitingApproval,
        ToolCallLedgerStatus.Denied => from == ToolCallLedgerStatus.Pending,
        ToolCallLedgerStatus.AwaitingApproval => from == ToolCallLedgerStatus.Pending,
        ToolCallLedgerStatus.Expired => from == ToolCallLedgerStatus.AwaitingApproval,
        _ => false,
    };
}
