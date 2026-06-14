using CodeSpace.Core.Services.Agents;
using CodeSpace.Messages.Agents;
using Shouldly;

namespace CodeSpace.UnitTests.Agents;

/// <summary>
/// Pins the ToolCallLedger lifecycle: Pending → terminal (Succeeded/Failed/Denied) for a synchronous call, or
/// Pending → AwaitingApproval → (Succeeded/Failed/Expired) for a durable approval (item D). The status-guarded CAS
/// in the service rejects every illegal transition, so the table is pinned exhaustively (including the illegal ones).
/// </summary>
[Trait("Category", "Unit")]
public class ToolCallLedgerStateMachineTests
{
    [Theory]
    // legal — synchronous resolution out of Pending
    [InlineData(ToolCallLedgerStatus.Pending, ToolCallLedgerStatus.Succeeded, true)]
    [InlineData(ToolCallLedgerStatus.Pending, ToolCallLedgerStatus.Failed, true)]
    [InlineData(ToolCallLedgerStatus.Pending, ToolCallLedgerStatus.Denied, true)]
    [InlineData(ToolCallLedgerStatus.Pending, ToolCallLedgerStatus.AwaitingApproval, true)]
    // legal — durable approval resolution (item D): claim for execution, expire, or fail (reject / interrupt) the parked row
    [InlineData(ToolCallLedgerStatus.AwaitingApproval, ToolCallLedgerStatus.Running, true)]
    [InlineData(ToolCallLedgerStatus.AwaitingApproval, ToolCallLedgerStatus.Failed, true)]
    [InlineData(ToolCallLedgerStatus.AwaitingApproval, ToolCallLedgerStatus.Expired, true)]
    // legal — an executing (claimed) row resolves to a terminal
    [InlineData(ToolCallLedgerStatus.Running, ToolCallLedgerStatus.Succeeded, true)]
    [InlineData(ToolCallLedgerStatus.Running, ToolCallLedgerStatus.Failed, true)]
    // illegal — the side effect runs ONLY after the Running claim; an approved row never succeeds straight from AwaitingApproval
    [InlineData(ToolCallLedgerStatus.AwaitingApproval, ToolCallLedgerStatus.Succeeded, false)]
    // illegal — a denial is synchronous-only; an approval can never re-pend or expire from Pending; Running is reached ONLY from AwaitingApproval
    [InlineData(ToolCallLedgerStatus.AwaitingApproval, ToolCallLedgerStatus.Denied, false)]
    [InlineData(ToolCallLedgerStatus.Pending, ToolCallLedgerStatus.Expired, false)]
    [InlineData(ToolCallLedgerStatus.Pending, ToolCallLedgerStatus.Running, false)]
    [InlineData(ToolCallLedgerStatus.Running, ToolCallLedgerStatus.Expired, false)]
    [InlineData(ToolCallLedgerStatus.Running, ToolCallLedgerStatus.Denied, false)]
    // illegal — Pending / AwaitingApproval / Running are never re-entrant targets (no re-pend / no going back)
    [InlineData(ToolCallLedgerStatus.Pending, ToolCallLedgerStatus.Pending, false)]
    [InlineData(ToolCallLedgerStatus.AwaitingApproval, ToolCallLedgerStatus.Pending, false)]
    [InlineData(ToolCallLedgerStatus.Running, ToolCallLedgerStatus.AwaitingApproval, false)]
    [InlineData(ToolCallLedgerStatus.Running, ToolCallLedgerStatus.Running, false)]
    // illegal — terminals are final
    [InlineData(ToolCallLedgerStatus.Succeeded, ToolCallLedgerStatus.Failed, false)]
    [InlineData(ToolCallLedgerStatus.Failed, ToolCallLedgerStatus.Succeeded, false)]
    [InlineData(ToolCallLedgerStatus.Denied, ToolCallLedgerStatus.Succeeded, false)]
    [InlineData(ToolCallLedgerStatus.Expired, ToolCallLedgerStatus.Succeeded, false)]
    [InlineData(ToolCallLedgerStatus.Succeeded, ToolCallLedgerStatus.AwaitingApproval, false)]
    public void IsLegalTransition(ToolCallLedgerStatus from, ToolCallLedgerStatus to, bool expected) =>
        ToolCallLedgerStateMachine.IsLegalTransition(from, to).ShouldBe(expected);

    [Theory]
    [InlineData(ToolCallLedgerStatus.Pending, false)]
    [InlineData(ToolCallLedgerStatus.AwaitingApproval, false)]
    [InlineData(ToolCallLedgerStatus.Running, false)]   // Running is the mid-execution hop — NOT terminal
    [InlineData(ToolCallLedgerStatus.Succeeded, true)]
    [InlineData(ToolCallLedgerStatus.Failed, true)]
    [InlineData(ToolCallLedgerStatus.Denied, true)]
    [InlineData(ToolCallLedgerStatus.Expired, true)]
    public void IsTerminal(ToolCallLedgerStatus status, bool expected) =>
        ToolCallLedgerStateMachine.IsTerminal(status).ShouldBe(expected);
}
