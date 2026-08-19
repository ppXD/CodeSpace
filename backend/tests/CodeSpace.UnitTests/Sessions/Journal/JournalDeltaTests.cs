using CodeSpace.Core.Services.Sessions.Journal;
using CodeSpace.Messages.Dtos.Sessions.Journal;
using CodeSpace.Messages.Enums;
using CodeSpace.Messages.Tasks.Timeline;
using Shouldly;

namespace CodeSpace.UnitTests.Sessions.Journal;

/// <summary>
/// 🟢 Unit: the ?since= delta trim. Pins what each turn keeps — the focused turn only its steps AFTER the client's
/// cursor, a non-focused TERMINAL turn none (its walk is finished, so the client's copy is already right), a non-focused
/// LIVE turn all of them (the client's cursor belongs to another run and says nothing about this one) — that the
/// structure and every turn's full StepCount survive so the client can detect divergence, and that an unrecognized
/// cursor trims NOTHING (the client re-syncs on the full set, never silently loses steps). No DB.
/// </summary>
[Trait("Category", "Unit")]
public class JournalDeltaTests
{
    private static readonly DateTimeOffset T = new(2026, 7, 3, 12, 0, 0, TimeSpan.Zero);

    /// <summary>A step whose cursor encodes an event at instant T+<paramref name="tick"/> — so the ordering is deterministic.</summary>
    private static JournalStep Step(string id, int tick)
    {
        var cursor = JournalCursor.Encode(new RunTimelineEvent
        {
            Id = id, Kind = "k", Title = id, Severity = TimelineSeverity.Info, Level = TimelineLevel.Detail,
            OccurredAt = T.AddSeconds(tick), Order = 0, SourceKey = "run-record",
        });

        return new JournalStep { Id = id, At = T.AddSeconds(tick), Kind = JournalStepKinds.Lifecycle, Title = id, Cursor = cursor };
    }

    private static JournalView View(IReadOnlyList<JournalStep> focusedSteps, IReadOnlyList<JournalStep>? collapsedSteps = null, WorkflowRunStatus collapsedStatus = WorkflowRunStatus.Success) => new()
    {
        SessionId = Guid.NewGuid(), Title = "t", Kind = WorkSessionKind.Task, Status = WorkSessionStatus.Open,
        Cursor = focusedSteps.Count > 0 ? focusedSteps[^1].Cursor : "",
        Turns = new[]
        {
            new JournalTurn { TurnIndex = 1, TurnRunId = Guid.NewGuid(), RunId = Guid.NewGuid(), Status = collapsedStatus, Focused = false, Steps = collapsedSteps ?? Array.Empty<JournalStep>(), StepCount = collapsedSteps?.Count ?? 0 },
            new JournalTurn { TurnIndex = 2, TurnRunId = Guid.NewGuid(), RunId = Guid.NewGuid(), Status = WorkflowRunStatus.Running, Focused = true, Steps = focusedSteps, StepCount = focusedSteps.Count },
        },
    };

    [Fact]
    public void Keeps_only_the_focused_turns_steps_after_the_cursor()
    {
        var s1 = Step("s1", 1);
        var s2 = Step("s2", 2);
        var s3 = Step("s3", 3);
        var view = View(new[] { s1, s2, s3 });

        var delta = JournalDelta.After(view, s1.Cursor);

        delta.Turns.Single(t => t.Focused).Steps.Select(s => s.Id).ShouldBe(new[] { "s2", "s3" }, "only the steps AFTER the client's cursor come back");
    }

    [Fact]
    public void The_newest_cursor_yields_no_new_steps()
    {
        var steps = new[] { Step("s1", 1), Step("s2", 2) };

        JournalDelta.After(View(steps), steps[^1].Cursor).Turns.Single(t => t.Focused).Steps.ShouldBeEmpty("nothing is newer than the head — an idle poll returns no steps");
    }

    [Fact]
    public void Preserves_the_focused_turns_attempt_ladder_through_the_trim()
    {
        // The pager must not vanish on a poll: the ?since trim only filters Steps, so the focused turn's Attempts ladder
        // (and its focused flag) survives verbatim — else the FE's attempt switcher would blink out on every delta.
        var s1 = Step("s1", 1);
        var s2 = Step("s2", 2);
        var ladder = new[]
        {
            new JournalAttempt { AttemptNumber = 1, RunId = Guid.NewGuid(), Status = WorkflowRunStatus.Failure, At = T, SourceType = "manual", IsLatest = false },
            new JournalAttempt { AttemptNumber = 2, RunId = Guid.NewGuid(), Status = WorkflowRunStatus.Running, At = T.AddMinutes(1), SourceType = "rerun", IsLatest = true, Focused = true },
        };
        var baseView = View(new[] { s1, s2 });
        var view = baseView with { Turns = new[] { baseView.Turns[0], baseView.Turns[1] with { Attempts = ladder } } };

        var focused = JournalDelta.After(view, s1.Cursor).Turns.Single(t => t.Focused);

        focused.Steps.Select(s => s.Id).ShouldBe(new[] { "s2" }, "steps are still trimmed to after the cursor");
        focused.Attempts.Select(a => a.AttemptNumber).ShouldBe(new[] { 1, 2 }, "the ladder survives the trim intact");
        focused.Attempts.Single(a => a.Focused).AttemptNumber.ShouldBe(2, "the focused flag survives too");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-cursor")]
    public void An_unrecognized_cursor_trims_nothing(string? since)
    {
        var steps = new[] { Step("s1", 1), Step("s2", 2) };

        JournalDelta.After(View(steps), since).Turns.Single(t => t.Focused).Steps.Count.ShouldBe(2, "an unrecognized cursor returns the FULL set — the client re-syncs, never loses steps");
    }

    [Fact]
    public void Preserves_the_focused_turns_total_step_count_for_self_heal()
    {
        // The self-heal signal: a delta trims Steps but KEEPS StepCount = the full total, so a client whose accumulated
        // count is short of it knows a below-cursor step exists and re-fetches full.
        var steps = new[] { Step("s1", 1), Step("s2", 2), Step("s3", 3) };

        var delta = JournalDelta.After(View(steps), steps[1].Cursor);

        var focused = delta.Turns.Single(t => t.Focused);
        focused.Steps.Count.ShouldBe(1, "only s3 is after s2");
        focused.StepCount.ShouldBe(3, "StepCount is the FULL total (3), not the trimmed count — the self-heal signal survives the delta");
    }

    [Fact]
    public void A_non_focused_terminal_turns_steps_are_dropped_but_its_total_survives()
    {
        // The finished history is what makes an unbounded poll expensive: a full fetch carries EVERY turn's steps (the
        // projector populates all of them), and a terminal turn's walk can never gain another step, so re-sending it on
        // every 2s poll is pure waste. The delta drops those steps and keeps StepCount, which is the client's proof that
        // its own copy is still complete — and its trigger to re-fetch in full if it isn't.
        var collapsedSteps = new[] { Step("c1", 1), Step("c2", 2) };
        var view = View(new[] { Step("s1", 1), Step("s2", 2) }, collapsedSteps, WorkflowRunStatus.Success);

        var delta = JournalDelta.After(view, Step("s1", 1).Cursor);

        delta.Turns.Count.ShouldBe(2, "the turns structure is unchanged — only steps are trimmed");
        var collapsed = delta.Turns.Single(t => !t.Focused);
        collapsed.Steps.ShouldBeEmpty("a terminal turn's walk is finished, so the client's copy is already correct");
        collapsed.StepCount.ShouldBe(2, "the full total survives — without it the client could not tell a trim from a truncation");
        delta.Cursor.ShouldBe(view.Cursor, "the head cursor is unchanged — the client advances to it");
    }

    [Theory]
    [InlineData(WorkflowRunStatus.Running)]
    [InlineData(WorkflowRunStatus.Pending)]
    public void A_non_focused_LIVE_turns_steps_are_kept_in_full(WorkflowRunStatus liveStatus)
    {
        // The client's single cursor is the FOCUSED turn's run. A concurrently-running turn is a different run whose
        // progress that cursor says nothing about, so there is no sound basis to trim it — sending it in full is correct,
        // not lazy. Trimming it would make every poll of a concurrent session fail the count check and re-fetch, which is
        // strictly worse than no delta at all.
        var collapsedSteps = new[] { Step("c1", 1), Step("c2", 2) };
        var view = View(new[] { Step("s1", 1), Step("s2", 2) }, collapsedSteps, liveStatus);

        var collapsed = JournalDelta.After(view, Step("s1", 1).Cursor).Turns.Single(t => !t.Focused);

        collapsed.Steps.Select(s => s.Id).ShouldBe(new[] { "c1", "c2" }, "a live turn's steps ride the delta in full");
        collapsed.StepCount.ShouldBe(2, "and its total agrees with what was sent, so the client's count check passes");
    }

}
