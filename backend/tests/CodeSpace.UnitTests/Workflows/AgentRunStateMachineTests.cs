using CodeSpace.Core.Services.Agents;
using CodeSpace.Messages.Enums;
using Shouldly;

namespace CodeSpace.UnitTests.Workflows;

[Trait("Category", "Unit")]
public class AgentRunStateMachineTests
{
    [Theory]
    // legal — the happy path + pre-start terminals
    [InlineData(AgentRunStatus.Queued, AgentRunStatus.Running, true)]
    [InlineData(AgentRunStatus.Queued, AgentRunStatus.Failed, true)]
    [InlineData(AgentRunStatus.Queued, AgentRunStatus.Cancelled, true)]
    [InlineData(AgentRunStatus.Queued, AgentRunStatus.TimedOut, true)]
    [InlineData(AgentRunStatus.Running, AgentRunStatus.Succeeded, true)]
    [InlineData(AgentRunStatus.Running, AgentRunStatus.Failed, true)]
    [InlineData(AgentRunStatus.Running, AgentRunStatus.Cancelled, true)]
    [InlineData(AgentRunStatus.Running, AgentRunStatus.TimedOut, true)]
    // legal — a would-be success re-graded to needs-review (Slice A1 completion contract); only from Running
    [InlineData(AgentRunStatus.Running, AgentRunStatus.NeedsReview, true)]
    // illegal — can't succeed without running
    [InlineData(AgentRunStatus.Queued, AgentRunStatus.Succeeded, false)]
    // illegal — can't need review without running (a pre-start run fails / cancels / times out, it never needs review)
    [InlineData(AgentRunStatus.Queued, AgentRunStatus.NeedsReview, false)]
    // illegal — no re-queue / no going back
    [InlineData(AgentRunStatus.Queued, AgentRunStatus.Queued, false)]
    [InlineData(AgentRunStatus.Running, AgentRunStatus.Queued, false)]
    // illegal — terminals are final (NeedsReview included)
    [InlineData(AgentRunStatus.Succeeded, AgentRunStatus.Running, false)]
    [InlineData(AgentRunStatus.Succeeded, AgentRunStatus.Failed, false)]
    [InlineData(AgentRunStatus.Succeeded, AgentRunStatus.NeedsReview, false)]
    [InlineData(AgentRunStatus.Failed, AgentRunStatus.Running, false)]
    [InlineData(AgentRunStatus.Cancelled, AgentRunStatus.Succeeded, false)]
    [InlineData(AgentRunStatus.TimedOut, AgentRunStatus.Running, false)]
    [InlineData(AgentRunStatus.NeedsReview, AgentRunStatus.Running, false)]
    [InlineData(AgentRunStatus.NeedsReview, AgentRunStatus.Succeeded, false)]
    public void IsLegalTransition(AgentRunStatus from, AgentRunStatus to, bool expected) =>
        AgentRunStateMachine.IsLegalTransition(from, to).ShouldBe(expected);

    [Theory]
    [InlineData(AgentRunStatus.Queued, false)]
    [InlineData(AgentRunStatus.Running, false)]
    [InlineData(AgentRunStatus.Succeeded, true)]
    [InlineData(AgentRunStatus.Failed, true)]
    [InlineData(AgentRunStatus.Cancelled, true)]
    [InlineData(AgentRunStatus.TimedOut, true)]
    [InlineData(AgentRunStatus.NeedsReview, true)]
    public void IsTerminal(AgentRunStatus status, bool expected) =>
        AgentRunStateMachine.IsTerminal(status).ShouldBe(expected);
}
