using CodeSpace.Core.Services.Sessions.Room;
using CodeSpace.Messages.Dtos.Sessions.Room;
using CodeSpace.Messages.Enums;
using Shouldly;

namespace CodeSpace.UnitTests.Sessions.Room;

/// <summary>
/// The turn-level capability resolver decides each action's <c>enabled</c> from the SAME terminal/non-terminal
/// boundary <c>WorkflowRunState</c> enforces on the write path — so a rendered action can't claim it's available when
/// the engine would reject it. Rerun is offered only on a finished turn; stop only on a live one; view-trace always.
/// </summary>
[Trait("Category", "Unit")]
public class RunActionCapabilityResolverTests
{
    [Theory]
    [InlineData(WorkflowRunStatus.Success, true)]
    [InlineData(WorkflowRunStatus.Failure, true)]
    [InlineData(WorkflowRunStatus.Cancelled, true)]
    [InlineData(WorkflowRunStatus.Pending, false)]
    [InlineData(WorkflowRunStatus.Enqueued, false)]
    [InlineData(WorkflowRunStatus.Running, false)]
    [InlineData(WorkflowRunStatus.Suspended, false)]
    public void Turn_actions_track_the_lifecycle_boundary(WorkflowRunStatus status, bool terminal)
    {
        var runId = Guid.NewGuid();

        var actions = new RunActionCapabilityResolver().ResolveTurnActions(runId, status);

        var rerun = actions.Single(a => a.Kind == RoomActionKind.RerunTurn);
        rerun.Enabled.ShouldBe(terminal, "rerun is offered only on a finished turn");
        rerun.Attempt.ShouldBeTrue("rerun forks an attempt of the turn");
        (rerun.DisabledReason == null).ShouldBe(terminal, "a disabled rerun carries a reason");

        var stop = actions.Single(a => a.Kind == RoomActionKind.Stop);
        stop.Enabled.ShouldBe(!terminal, "stop is offered only on a live turn");
        (stop.DisabledReason == null).ShouldBe(!terminal);

        // Continue resumes IN PLACE — offered ONLY on a stopped (Cancelled) or failed turn (Success has nothing to
        // resume; an active turn stops first; a Suspended turn resumes via its wait), matching ContinueRunAsync.
        var continuable = status is WorkflowRunStatus.Cancelled or WorkflowRunStatus.Failure;
        var cont = actions.Single(a => a.Kind == RoomActionKind.Continue);
        cont.Enabled.ShouldBe(continuable, "continue is offered only on a stopped or failed turn");
        cont.Attempt.ShouldBeFalse("continue revives the SAME run in place — it is not a fresh attempt");
        (cont.DisabledReason == null).ShouldBe(continuable, "a disabled continue carries a reason");

        var trace = actions.Single(a => a.Kind == RoomActionKind.OpenTrace);
        trace.Enabled.ShouldBeTrue("view-trace is always available");
        trace.Target.ShouldBe(runId.ToString());
    }
}
