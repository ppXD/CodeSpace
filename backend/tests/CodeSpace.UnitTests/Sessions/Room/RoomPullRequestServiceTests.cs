using CodeSpace.Core.Services.Sessions.Room;
using CodeSpace.Core.Services.Supervisor;
using CodeSpace.Messages.Agents;
using Shouldly;

namespace CodeSpace.UnitTests.Sessions.Room;

/// <summary>
/// 🟢 Unit: <see cref="RoomPullRequestService.DeriveTitleAndBody"/> is a pure function over the decision tape (no DB) —
/// pinned directly (InternalsVisibleTo) rather than only through the DB-heavy integration coverage the rest of the
/// service needs (WorkflowRun/Repository/PublishManifest reads).
/// </summary>
[Trait("Category", "Unit")]
public class RoomPullRequestServiceTests
{
    [Fact]
    public void Title_is_the_stop_summarys_first_line_and_body_is_the_summary_in_full()
    {
        var decisions = new[] { StopDecision("""{"outcome":"completed","summary":"Fix the flaky retry timer"}""") };

        var (title, body) = RoomPullRequestService.DeriveTitleAndBody(decisions);

        title.ShouldBe("Fix the flaky retry timer");
        body.ShouldBe("Fix the flaky retry timer");
    }

    [Fact]
    public void Title_takes_only_the_first_line_of_a_multi_line_summary_while_body_keeps_it_all()
    {
        var decisions = new[] { StopDecision("""{"outcome":"completed","summary":"Fix the flaky retry timer\n\n- widened the backoff window\n- added a regression test"}""") };

        var (title, body) = RoomPullRequestService.DeriveTitleAndBody(decisions);

        title.ShouldBe("Fix the flaky retry timer");
        body.ShouldBe("Fix the flaky retry timer\n\n- widened the backoff window\n- added a regression test");
    }

    [Fact]
    public void Title_is_truncated_past_100_chars_but_body_is_never_truncated()
    {
        var longLine = new string('x', 150);
        var decisions = new[] { StopDecision($$"""{"outcome":"completed","summary":"{{longLine}}"}""") };

        var (title, body) = RoomPullRequestService.DeriveTitleAndBody(decisions);

        title.Length.ShouldBe(100);
        body!.Length.ShouldBe(150);
    }

    [Fact]
    public void Falls_back_to_a_generic_title_and_no_body_when_the_stop_has_no_summary()
    {
        var decisions = new[] { StopDecision("""{"outcome":"completed"}""") };

        var (title, body) = RoomPullRequestService.DeriveTitleAndBody(decisions);

        title.ShouldBe("Merge agent changes");
        body.ShouldBeNull();
    }

    [Fact]
    public void Falls_back_to_a_generic_title_when_the_tape_has_no_stop_decision_at_all()
    {
        // A degenerate but real terminal shape (e.g. a run whose published work came from an earlier turn and the
        // reachable tape has no stop row) — must never throw, just degrade to the generic framing.
        var decisions = Array.Empty<SupervisorPriorDecision>();

        var (title, body) = RoomPullRequestService.DeriveTitleAndBody(decisions);

        title.ShouldBe("Merge agent changes");
        body.ShouldBeNull();
    }

    [Fact]
    public void Uses_the_LATEST_stop_when_the_tape_somehow_carries_more_than_one()
    {
        var decisions = new[]
        {
            StopDecision("""{"outcome":"no-decision","summary":"an earlier abandoned stop"}""", sequence: 1),
            StopDecision("""{"outcome":"completed","summary":"the real final summary"}""", sequence: 2),
        };

        var (title, _) = RoomPullRequestService.DeriveTitleAndBody(decisions);

        title.ShouldBe("the real final summary");
    }

    private static SupervisorPriorDecision StopDecision(string outcomeJson, long sequence = 1) => new()
    {
        Id = Guid.NewGuid(),
        Sequence = sequence,
        DecisionKind = SupervisorDecisionKinds.Stop,
        Status = SupervisorDecisionStatus.Succeeded,
        PayloadJson = "{}",
        OutcomeJson = outcomeJson,
    };
}
