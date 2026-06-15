using CodeSpace.Messages.Agents;

namespace CodeSpace.Core.Services.Supervisor;

/// <summary>
/// The <c>SupervisorDecisionRecord</c> lifecycle as a pure state machine. Mirrors <c>ToolCallLedgerStateMachine</c>:
/// pure + side-effect-free → unit-tested exhaustively, and the single source of truth the service's status-guarded CAS
/// consults to REJECT an illegal/lost transition rather than silently apply it.
///
/// <para>Shape: <c>Pending</c> → <c>Running</c> → (<c>Succeeded</c> | <c>Failed</c>) is the canonical path; an optional
/// <c>Pending</c> → <c>AwaitingApproval</c> → <c>Running</c> detour parks for a human (a later HITL slice); a stale
/// undecided row expires (<c>Pending</c> → <c>Expired</c> via the E1 reaper, or <c>AwaitingApproval</c> → <c>Expired</c>).
/// </para>
///
/// <para><b>The <c>Pending → Running</c> claim hop is the single-winner execution gate (must-fix #2):</b> a terminal is
/// reachable ONLY from <c>Running</c>, never straight from <c>Pending</c>. So a decision's side effect runs ONLY after a
/// caller wins the status-guarded CAS into <c>Running</c> — the synchronous path does NOT rely on the INSERT alone. Of N
/// concurrent executors of the same claimed (run, key), exactly one flips <c>Pending → Running</c> and runs the side
/// effect; every loser sees the row already <c>Running</c>/terminal and replays. There is deliberately no
/// <c>Pending → Succeeded</c>/<c>Pending → Failed</c> shortcut.</para>
/// </summary>
public static class SupervisorDecisionStateMachine
{
    /// <summary>True when <paramref name="status"/> is terminal (no further transition out). <c>Running</c> is NOT terminal — it's the mid-execution hop between the claim and the outcome.</summary>
    public static bool IsTerminal(SupervisorDecisionStatus status) =>
        status is SupervisorDecisionStatus.Succeeded or SupervisorDecisionStatus.Failed or SupervisorDecisionStatus.Expired;

    /// <summary>
    /// True when moving <paramref name="from"/> → <paramref name="to"/> is allowed. Out of <c>Pending</c> a decision may
    /// be claimed for execution (→ <c>Running</c>), park for approval (→ <c>AwaitingApproval</c>), or be reaped while
    /// stale (→ <c>Expired</c>). Out of <c>AwaitingApproval</c> it may be claimed (→ <c>Running</c>) or expire. Out of
    /// <c>Running</c> it succeeds or fails. A terminal never transitions out, and <c>Pending</c> is never a target (no
    /// re-pend). Crucially a terminal is reachable ONLY from <c>Running</c> — the claim hop is mandatory before the side
    /// effect (no <c>Pending → Succeeded</c> shortcut).
    /// </summary>
    public static bool IsLegalTransition(SupervisorDecisionStatus from, SupervisorDecisionStatus to) => to switch
    {
        SupervisorDecisionStatus.Running => from is SupervisorDecisionStatus.Pending or SupervisorDecisionStatus.AwaitingApproval,
        SupervisorDecisionStatus.AwaitingApproval => from == SupervisorDecisionStatus.Pending,
        SupervisorDecisionStatus.Succeeded => from == SupervisorDecisionStatus.Running,
        SupervisorDecisionStatus.Failed => from == SupervisorDecisionStatus.Running,
        SupervisorDecisionStatus.Expired => from is SupervisorDecisionStatus.Pending or SupervisorDecisionStatus.AwaitingApproval,
        _ => false,
    };
}
