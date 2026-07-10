using CodeSpace.Core.Services.Supervisor;
using CodeSpace.Messages.Agents;
using Shouldly;

namespace CodeSpace.UnitTests.Supervisor;

/// <summary>
/// 🟢 Unit: <see cref="SupervisorPullRequestOpener.DeriveTitleAndBody"/> is a pure function over the decision tape (no DB) —
/// pinned directly (InternalsVisibleTo) rather than only through the DB-heavy integration coverage the rest of the
/// opener needs (WorkflowRun/Repository/PublishManifest reads).
/// </summary>
[Trait("Category", "Unit")]
public class SupervisorPullRequestOpenerTests
{
    [Fact]
    public void Title_is_the_stop_summarys_first_line_and_body_is_the_summary_in_full()
    {
        var decisions = new[] { StopDecision("""{"outcome":"completed","summary":"Fix the flaky retry timer"}""") };

        var (title, body) = SupervisorPullRequestOpener.DeriveTitleAndBody(decisions);

        title.ShouldBe("Fix the flaky retry timer");
        body.ShouldBe("Fix the flaky retry timer");
    }

    [Fact]
    public void Title_takes_only_the_first_line_of_a_multi_line_summary_while_body_keeps_it_all()
    {
        var decisions = new[] { StopDecision("""{"outcome":"completed","summary":"Fix the flaky retry timer\n\n- widened the backoff window\n- added a regression test"}""") };

        var (title, body) = SupervisorPullRequestOpener.DeriveTitleAndBody(decisions);

        title.ShouldBe("Fix the flaky retry timer");
        body.ShouldBe("Fix the flaky retry timer\n\n- widened the backoff window\n- added a regression test");
    }

    [Fact]
    public void Title_is_truncated_past_100_chars_but_body_is_never_truncated()
    {
        var longLine = new string('x', 150);
        var decisions = new[] { StopDecision($$"""{"outcome":"completed","summary":"{{longLine}}"}""") };

        var (title, body) = SupervisorPullRequestOpener.DeriveTitleAndBody(decisions);

        title.Length.ShouldBe(100);
        body!.Length.ShouldBe(150);
    }

    [Fact]
    public void Falls_back_to_a_generic_title_and_no_body_when_the_stop_has_no_summary()
    {
        var decisions = new[] { StopDecision("""{"outcome":"completed"}""") };

        var (title, body) = SupervisorPullRequestOpener.DeriveTitleAndBody(decisions);

        title.ShouldBe("Merge agent changes");
        body.ShouldBeNull();
    }

    [Fact]
    public void Falls_back_to_a_generic_title_when_the_tape_has_no_stop_decision_at_all()
    {
        // A degenerate but real terminal shape (e.g. a run whose published work came from an earlier turn and the
        // reachable tape has no stop row) — must never throw, just degrade to the generic framing.
        var decisions = Array.Empty<SupervisorPriorDecision>();

        var (title, body) = SupervisorPullRequestOpener.DeriveTitleAndBody(decisions);

        title.ShouldBe("Merge agent changes");
        body.ShouldBeNull();
    }

    [Fact]
    public void Falls_back_to_a_generic_title_when_the_first_line_is_blank_but_the_summary_is_not()
    {
        // Deep-audit finding: the summary as a whole passes the outer IsNullOrWhiteSpace check (there IS real
        // content), but its FIRST line is whitespace-only — the post-truncation "title.Length > 0" fallback branch
        // this guards against was never exercised by any existing case.
        var decisions = new[] { StopDecision("""{"outcome":"completed","summary":"   \nthe real content is on the second line"}""") };

        var (title, body) = SupervisorPullRequestOpener.DeriveTitleAndBody(decisions);

        title.ShouldBe("Merge agent changes", "the first line trims to nothing — the title must fall back, never ship a blank PR title");
        body.ShouldBe("   \nthe real content is on the second line", "the body is never touched by the title's fallback — it carries the summary verbatim");
    }

    [Fact]
    public void Uses_the_LATEST_stop_when_the_tape_somehow_carries_more_than_one()
    {
        var decisions = new[]
        {
            StopDecision("""{"outcome":"no-decision","summary":"an earlier abandoned stop"}""", sequence: 1),
            StopDecision("""{"outcome":"completed","summary":"the real final summary"}""", sequence: 2),
        };

        var (title, _) = SupervisorPullRequestOpener.DeriveTitleAndBody(decisions);

        title.ShouldBe("the real final summary");
    }

    // ── DC-2b: currentTurnStopSummary — the live-turn substitution's own rejected-stop summary ──

    [Fact]
    public void The_current_turns_stop_summary_is_used_when_the_tape_has_no_persisted_stop_at_all()
    {
        // The common first-forced-publish shape: THIS turn's stop was rejected-and-substituted, so it never
        // reached the tape — priorDecisions alone would fall back to the generic title without this parameter.
        var (title, body) = SupervisorPullRequestOpener.DeriveTitleAndBody(Array.Empty<SupervisorPriorDecision>(), currentTurnStopSummary: "Fixed the flaky retry timer");

        title.ShouldBe("Fixed the flaky retry timer");
        body.ShouldBe("Fixed the flaky retry timer");
    }

    [Fact]
    public void The_current_turns_stop_summary_wins_over_an_older_persisted_stop_on_the_tape()
    {
        var decisions = new[] { StopDecision("""{"outcome":"no-decision","summary":"an earlier abandoned stop"}""") };

        var (title, _) = SupervisorPullRequestOpener.DeriveTitleAndBody(decisions, currentTurnStopSummary: "the real current summary");

        title.ShouldBe("the real current summary");
    }

    [Fact]
    public void A_blank_current_turn_stop_summary_falls_back_to_scanning_the_tape()
    {
        var decisions = new[] { StopDecision("""{"outcome":"completed","summary":"Room's own post-terminal read"}""") };

        var (title, _) = SupervisorPullRequestOpener.DeriveTitleAndBody(decisions, currentTurnStopSummary: "");

        title.ShouldBe("Room's own post-terminal read", "Room never passes a live-turn summary — blank must defer to the tape scan, not win as an empty title");
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
