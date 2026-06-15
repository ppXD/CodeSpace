using CodeSpace.Core.Services.Supervisor;
using CodeSpace.Messages.Agents;
using Shouldly;

namespace CodeSpace.UnitTests.Agents;

/// <summary>
/// 🟢 Unit: pins the SupervisorDecisionRecord lifecycle EXHAUSTIVELY. The canonical path is
/// Pending → Running → (Succeeded/Failed); an optional Pending → AwaitingApproval → Running detour parks for a human; a
/// stale undecided row expires (Pending → Expired via the E1 reaper, or AwaitingApproval → Expired). The status-guarded
/// CAS in <see cref="SupervisorDecisionLog"/> consults this to REJECT every illegal transition (incl. double-Running,
/// terminal→anything, skip-Running), so the table is pinned exhaustively (legal + illegal). The MUST-FIX-#2 invariant —
/// a terminal is reachable ONLY from Running, never straight from Pending — is pinned here.
/// </summary>
[Trait("Category", "Unit")]
public class SupervisorDecisionStateMachineTests
{
    [Theory]
    // legal — the claim hop (the single-winner gate BEFORE the side effect, must-fix #2)
    [InlineData(SupervisorDecisionStatus.Pending, SupervisorDecisionStatus.Running, true)]
    // legal — optional approval detour out of Pending
    [InlineData(SupervisorDecisionStatus.Pending, SupervisorDecisionStatus.AwaitingApproval, true)]
    // legal — a stale undecided Pending row is reaped (the E1 reaper target)
    [InlineData(SupervisorDecisionStatus.Pending, SupervisorDecisionStatus.Expired, true)]
    // legal — out of AwaitingApproval: claim for execution, or expire (undecided)
    [InlineData(SupervisorDecisionStatus.AwaitingApproval, SupervisorDecisionStatus.Running, true)]
    [InlineData(SupervisorDecisionStatus.AwaitingApproval, SupervisorDecisionStatus.Expired, true)]
    // legal — an executing (claimed) row resolves to a terminal
    [InlineData(SupervisorDecisionStatus.Running, SupervisorDecisionStatus.Succeeded, true)]
    [InlineData(SupervisorDecisionStatus.Running, SupervisorDecisionStatus.Failed, true)]
    // ILLEGAL (must-fix #2) — a terminal is reachable ONLY from Running; there is NO Pending → terminal shortcut.
    // The side effect runs only after the Pending → Running claim is won.
    [InlineData(SupervisorDecisionStatus.Pending, SupervisorDecisionStatus.Succeeded, false)]
    [InlineData(SupervisorDecisionStatus.Pending, SupervisorDecisionStatus.Failed, false)]
    // illegal — an approved row never succeeds/fails straight from AwaitingApproval (it must be claimed → Running first)
    [InlineData(SupervisorDecisionStatus.AwaitingApproval, SupervisorDecisionStatus.Succeeded, false)]
    [InlineData(SupervisorDecisionStatus.AwaitingApproval, SupervisorDecisionStatus.Failed, false)]
    // illegal — Running is NOT a re-pend/re-park/expire target; a claimed row only resolves to Succeeded/Failed
    [InlineData(SupervisorDecisionStatus.Running, SupervisorDecisionStatus.Expired, false)]
    [InlineData(SupervisorDecisionStatus.Running, SupervisorDecisionStatus.AwaitingApproval, false)]
    // illegal — double-Running (the claim hop is single-shot; a second claim must lose, never re-enter Running)
    [InlineData(SupervisorDecisionStatus.Running, SupervisorDecisionStatus.Running, false)]
    // illegal — Pending / AwaitingApproval are never re-entrant targets (no re-pend, no going back)
    [InlineData(SupervisorDecisionStatus.Pending, SupervisorDecisionStatus.Pending, false)]
    [InlineData(SupervisorDecisionStatus.Running, SupervisorDecisionStatus.Pending, false)]
    [InlineData(SupervisorDecisionStatus.AwaitingApproval, SupervisorDecisionStatus.Pending, false)]
    [InlineData(SupervisorDecisionStatus.AwaitingApproval, SupervisorDecisionStatus.AwaitingApproval, false)]
    // illegal — terminals are final (terminal → anything is rejected)
    [InlineData(SupervisorDecisionStatus.Succeeded, SupervisorDecisionStatus.Failed, false)]
    [InlineData(SupervisorDecisionStatus.Failed, SupervisorDecisionStatus.Succeeded, false)]
    [InlineData(SupervisorDecisionStatus.Expired, SupervisorDecisionStatus.Running, false)]
    [InlineData(SupervisorDecisionStatus.Succeeded, SupervisorDecisionStatus.Running, false)]
    [InlineData(SupervisorDecisionStatus.Expired, SupervisorDecisionStatus.Succeeded, false)]
    public void IsLegalTransition(SupervisorDecisionStatus from, SupervisorDecisionStatus to, bool expected) =>
        SupervisorDecisionStateMachine.IsLegalTransition(from, to).ShouldBe(expected);

    [Theory]
    [InlineData(SupervisorDecisionStatus.Pending, false)]
    [InlineData(SupervisorDecisionStatus.AwaitingApproval, false)]
    [InlineData(SupervisorDecisionStatus.Running, false)]   // Running is the mid-execution hop — NOT terminal
    [InlineData(SupervisorDecisionStatus.Succeeded, true)]
    [InlineData(SupervisorDecisionStatus.Failed, true)]
    [InlineData(SupervisorDecisionStatus.Expired, true)]
    public void IsTerminal(SupervisorDecisionStatus status, bool expected) =>
        SupervisorDecisionStateMachine.IsTerminal(status).ShouldBe(expected);
}
