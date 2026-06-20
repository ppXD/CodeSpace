using CodeSpace.Messages.Agents;

namespace CodeSpace.Core.Services.Agents;

/// <summary>
/// The <c>ToolCallLedger</c> lifecycle as a pure state machine: <c>Pending</c> → terminal
/// (<c>Succeeded</c> | <c>Failed</c> | <c>Denied</c>) for a synchronously-resolved call, or
/// <c>Pending</c> → <c>AwaitingApproval</c> → <c>Running</c> → (<c>Succeeded</c> | <c>Failed</c>) for a
/// durable mid-turn approval that is approved then executed, or <c>AwaitingApproval</c> → <c>Expired</c> when the
/// reaper expires an undecided approval (item D). The <c>Running</c> hop is the single-winner execution claim: out of
/// <c>AwaitingApproval</c> exactly one executor flips the row to <c>Running</c> BEFORE running the side effect, so a
/// concurrent racer that lost the CAS never re-runs it. The single source of truth for which transitions are legal, so
/// the service's status-guarded CAS can REJECT an illegal/lost transition rather than silently apply it. Mirrors
/// <see cref="AgentRunStateMachine"/>: pure + side-effect-free → unit-tested exhaustively.
/// </summary>
public static class ToolCallLedgerStateMachine
{
    /// <summary>True when <paramref name="status"/> is terminal (no further transition out of it). <c>Running</c> is NOT terminal — it's the mid-execution hop between an approved <c>AwaitingApproval</c> and its terminal.</summary>
    public static bool IsTerminal(ToolCallLedgerStatus status) =>
        status is ToolCallLedgerStatus.Succeeded or ToolCallLedgerStatus.Failed or ToolCallLedgerStatus.Denied or ToolCallLedgerStatus.Expired;

    /// <summary>
    /// True when moving <paramref name="from"/> → <paramref name="to"/> is allowed: a call may succeed / fail /
    /// be denied / pause for approval out of <c>Pending</c>; out of <c>AwaitingApproval</c> it may be claimed for
    /// execution (→ <c>Running</c>) or expire (the reaper); out of <c>Running</c> it succeeds / fails. A synchronous
    /// (Pending) call still resolves straight to its terminal without the <c>Running</c> hop. A denial is a
    /// synchronous-only verdict, an approval can never re-pend. Nothing transitions out of a terminal, and
    /// <c>Pending</c> is never a target.
    /// </summary>
    public static bool IsLegalTransition(ToolCallLedgerStatus from, ToolCallLedgerStatus to) => to switch
    {
        // Pending → Succeeded (synchronous call) and Running → Succeeded (executed after approval) are the side-effect
        // paths. AwaitingApproval → Succeeded is the DECISION-answer edge (Decision substrate D2): a decision.request has
        // NO side effect to execute — the human's typed answer IS the terminal result — so it resolves straight out of
        // AwaitingApproval, never through the Running execution claim. The edge is taken ONLY by TryAnswerDecisionAsync,
        // which guards on tool_kind == 'decision.request', so a real side-effecting approval row can never reach Succeeded
        // without its Running execution hop.
        ToolCallLedgerStatus.Succeeded => from is ToolCallLedgerStatus.Pending or ToolCallLedgerStatus.Running or ToolCallLedgerStatus.AwaitingApproval,
        ToolCallLedgerStatus.Failed => from is ToolCallLedgerStatus.Pending or ToolCallLedgerStatus.AwaitingApproval or ToolCallLedgerStatus.Running,
        ToolCallLedgerStatus.Denied => from == ToolCallLedgerStatus.Pending,
        ToolCallLedgerStatus.AwaitingApproval => from == ToolCallLedgerStatus.Pending,
        ToolCallLedgerStatus.Running => from == ToolCallLedgerStatus.AwaitingApproval,
        ToolCallLedgerStatus.Expired => from == ToolCallLedgerStatus.AwaitingApproval,
        _ => false,
    };
}
