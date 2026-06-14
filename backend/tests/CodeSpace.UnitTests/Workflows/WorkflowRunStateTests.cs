using CodeSpace.Core.Services.Workflows;
using CodeSpace.Messages.Enums;
using Shouldly;

namespace CodeSpace.UnitTests.Workflows;

/// <summary>
/// Pins the operator-cancel boundary: which run statuses an operator may flip to Cancelled and which
/// are terminal no-ops. Drives the CancelRunAsync CAS guard (cancellable → CAS; terminal → idempotent
/// no-op). Mirrors AgentRunStateMachineTests.
/// </summary>
[Trait("Category", "Unit")]
public class WorkflowRunStateTests
{
    [Theory]
    // terminal — a cancel is a no-op on these
    [InlineData(WorkflowRunStatus.Success, true)]
    [InlineData(WorkflowRunStatus.Failure, true)]
    [InlineData(WorkflowRunStatus.Cancelled, true)]
    // non-terminal — live states a cancel can flip
    [InlineData(WorkflowRunStatus.Pending, false)]
    [InlineData(WorkflowRunStatus.Enqueued, false)]
    [InlineData(WorkflowRunStatus.Running, false)]
    [InlineData(WorkflowRunStatus.Suspended, false)]
    public void IsTerminal(WorkflowRunStatus status, bool expected) =>
        WorkflowRunState.IsTerminal(status).ShouldBe(expected);

    [Theory]
    // every non-terminal state is cancellable — the PRIMARY target is a Suspended fan-out
    [InlineData(WorkflowRunStatus.Pending, true)]
    [InlineData(WorkflowRunStatus.Enqueued, true)]
    [InlineData(WorkflowRunStatus.Running, true)]
    [InlineData(WorkflowRunStatus.Suspended, true)]
    // terminals can't be cancelled
    [InlineData(WorkflowRunStatus.Success, false)]
    [InlineData(WorkflowRunStatus.Failure, false)]
    [InlineData(WorkflowRunStatus.Cancelled, false)]
    public void IsCancellable(WorkflowRunStatus status, bool expected) =>
        WorkflowRunState.IsCancellable(status).ShouldBe(expected);
}
