using CodeSpace.Core.Services.Sessions.Journal;
using CodeSpace.Messages.Dtos.Sessions.Journal;
using CodeSpace.Messages.Enums;
using CodeSpace.Messages.Tasks.Timeline;
using Shouldly;

namespace CodeSpace.UnitTests.Sessions.Journal;

/// <summary>
/// 🟢 Unit: the ?since= delta trim. Pins that it keeps only the focused turn's steps AFTER the client's cursor (so a
/// live poll re-sends only new steps), leaves the structure + collapsed turns intact, and — on an unrecognized cursor —
/// trims NOTHING (the client re-syncs on the full set, never silently loses steps). No DB.
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

    private static JournalView View(IReadOnlyList<JournalStep> focusedSteps, IReadOnlyList<JournalStep>? collapsedSteps = null) => new()
    {
        SessionId = Guid.NewGuid(), Title = "t", Kind = WorkSessionKind.Task, Status = WorkSessionStatus.Open,
        Cursor = focusedSteps.Count > 0 ? focusedSteps[^1].Cursor : "",
        Turns = new[]
        {
            new JournalTurn { TurnIndex = 1, TurnRunId = Guid.NewGuid(), RunId = Guid.NewGuid(), Status = WorkflowRunStatus.Success, Focused = false, Steps = collapsedSteps ?? Array.Empty<JournalStep>() },
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
    public void Trims_only_the_focused_turn_never_a_non_focused_one()
    {
        // A (hypothetical) non-focused turn with steps must be left ENTIRELY untouched — After only ever filters the
        // focused turn, so the delta can't accidentally drop steps from a card.
        var collapsedSteps = new[] { Step("c1", 1), Step("c2", 2) };
        var view = View(new[] { Step("s1", 1), Step("s2", 2) }, collapsedSteps);

        var delta = JournalDelta.After(view, Step("s1", 1).Cursor);

        delta.Turns.Count.ShouldBe(2, "the turns structure is unchanged");
        delta.Turns.Single(t => !t.Focused).Steps.Select(s => s.Id).ShouldBe(new[] { "c1", "c2" }, "a non-focused turn's steps are never trimmed");
        delta.Cursor.ShouldBe(view.Cursor, "the head cursor is unchanged — the client advances to it");
    }
}
