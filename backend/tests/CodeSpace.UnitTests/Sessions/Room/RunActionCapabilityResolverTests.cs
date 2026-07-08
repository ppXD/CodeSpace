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

    [Fact]
    public void OpenPullRequest_is_omitted_when_no_publish_signal_is_supplied()
    {
        // The light collapsed-card path passes no signal (it skips the extra ledger + manifest reads) — the action
        // must not appear at all, not just render disabled, so a collapsed card's action list stays byte-identical
        // to pre-PR-6.
        var actions = new RunActionCapabilityResolver().ResolveTurnActions(Guid.NewGuid(), WorkflowRunStatus.Success);

        actions.ShouldNotContain(a => a.Kind == RoomActionKind.OpenPullRequest);
    }

    [Fact]
    public void OpenPullRequest_is_disabled_when_the_run_published_no_branch()
    {
        var actions = new RunActionCapabilityResolver().ResolveTurnActions(Guid.NewGuid(), WorkflowRunStatus.Success, new RoomPublishState { HasPublishedBranch = false });

        var action = actions.Single(a => a.Kind == RoomActionKind.OpenPullRequest);
        action.Enabled.ShouldBeFalse();
        action.DisabledReason.ShouldNotBeNull();
        action.Label.ShouldBe("Open PR");
        action.Url.ShouldBeNull();
    }

    [Fact]
    public void OpenPullRequest_is_enabled_and_labelled_Open_when_a_branch_exists_with_no_PR_yet()
    {
        var actions = new RunActionCapabilityResolver().ResolveTurnActions(Guid.NewGuid(), WorkflowRunStatus.Success, new RoomPublishState { HasPublishedBranch = true });

        var action = actions.Single(a => a.Kind == RoomActionKind.OpenPullRequest);
        action.Enabled.ShouldBeTrue();
        action.DisabledReason.ShouldBeNull();
        action.Label.ShouldBe("Open PR");
        action.Url.ShouldBeNull();
    }

    [Fact]
    public void OpenPullRequest_renders_as_View_PR_once_one_is_already_opened()
    {
        var actions = new RunActionCapabilityResolver().ResolveTurnActions(Guid.NewGuid(), WorkflowRunStatus.Success, new RoomPublishState { HasPublishedBranch = true, OpenedPullRequestUrl = "https://github.com/o/r/pull/1" });

        var action = actions.Single(a => a.Kind == RoomActionKind.OpenPullRequest);
        action.Enabled.ShouldBeTrue();
        action.Label.ShouldBe("View PR");
        action.Url.ShouldBe("https://github.com/o/r/pull/1");
    }

    [Fact]
    public void OpenPullRequest_stays_a_View_PR_link_even_if_HasPublishedBranch_somehow_reads_false()
    {
        // Defensive: an already-opened PR's link must always be actionable regardless of what HasPublishedBranch
        // says (the manifest recording a PR IS evidence a branch existed) — never strand a user on a disabled button
        // pointing at a PR that demonstrably already exists.
        var actions = new RunActionCapabilityResolver().ResolveTurnActions(Guid.NewGuid(), WorkflowRunStatus.Success, new RoomPublishState { HasPublishedBranch = false, OpenedPullRequestUrl = "https://github.com/o/r/pull/1" });

        var action = actions.Single(a => a.Kind == RoomActionKind.OpenPullRequest);
        action.Enabled.ShouldBeTrue();
        action.Label.ShouldBe("View PR");
    }
}
