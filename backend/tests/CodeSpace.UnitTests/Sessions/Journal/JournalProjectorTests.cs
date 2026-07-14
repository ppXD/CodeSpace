using CodeSpace.Core.Services.Sessions;
using CodeSpace.Core.Services.Sessions.Journal;
using CodeSpace.Messages.Dtos.Sessions;
using CodeSpace.Messages.Dtos.Sessions.Journal;
using CodeSpace.Messages.Enums;
using CodeSpace.Messages.Tasks.Timeline;
using Shouldly;

namespace CodeSpace.UnitTests.Sessions.Journal;

/// <summary>
/// 🟢 Unit: the journal projector's turn assembly — over a faked session skeleton + a faked walk (so this pins the
/// ASSEMBLY, not the walk/DB). Only the FOCUSED turn is walked into steps; the rest are light cards (no steps). The
/// anchor selects the focused turn (a run-entry anchors that run's turn; a session-entry focuses the last); the view
/// cursor is the focused turn's newest step cursor (the delta head); a foreign session projects to null.
/// </summary>
[Trait("Category", "Unit")]
public class JournalProjectorTests
{
    private static readonly Guid Team = Guid.NewGuid();

    private static SessionTurn Turn(int index, Guid runId, string? user = "do the thing", string? result = "done", WorkflowRunStatus status = WorkflowRunStatus.Success) => new()
    {
        TurnIndex = index, TurnRunId = runId, RunId = runId, UserMessage = user, RunStatus = status, Result = result,
        HasPendingDecision = false, CreatedDate = DateTimeOffset.UtcNow, AttemptCount = 1,
    };

    private static SessionDetail Detail(int? anchor, params SessionTurn[] turns) => new()
    {
        Id = Guid.NewGuid(), Title = "A thread", Kind = WorkSessionKind.Task, Status = WorkSessionStatus.Open,
        CreatedDate = DateTimeOffset.UtcNow, AnchorTurnIndex = anchor, Turns = turns,
    };

    private static JournalStep Step(string id, string cursor) => new()
    {
        Id = id, At = DateTimeOffset.UtcNow, Kind = JournalStepKinds.Decision, Title = "Supervisor planned the work", Cursor = cursor,
    };

    /// <summary>A reran turn: a lineage of attempts (oldest first), the turn's RunId = the newest, TurnRunId = the oldest (the root).</summary>
    private static SessionTurn RerunTurn(int index, params (Guid RunId, WorkflowRunStatus Status)[] attempts)
    {
        var ladder = attempts.Select((a, i) => new SessionTurnAttempt
        {
            RunId = a.RunId, AttemptNumber = i + 1, Status = a.Status, SourceType = i == 0 ? "manual" : "rerun",
            RerunFromNodeId = i == 0 ? null : "integrate",   // a rerun-from-node forks at a step; the original has no fork point
            Error = a.Status == WorkflowRunStatus.Failure ? "the tests timed out" : null,
            CreatedDate = DateTimeOffset.UtcNow.AddMinutes(i), IsLatest = i == attempts.Length - 1,
        }).ToList();

        return new SessionTurn
        {
            TurnIndex = index, TurnRunId = attempts[0].RunId, RunId = attempts[^1].RunId, UserMessage = "reran work",
            RunStatus = attempts[^1].Status, Result = "latest result", HasPendingDecision = false,
            CreatedDate = DateTimeOffset.UtcNow, AttemptCount = attempts.Length, Attempts = ladder,
        };
    }

    private static JournalProjector Projector(SessionDetail? detail, Func<Guid, IReadOnlyList<JournalStep>?>? steps = null) =>
        new(new FakeSessions(detail), new FakeWalk(steps ?? (_ => Array.Empty<JournalStep>())));

    [Fact]
    public async Task Walks_only_the_focused_turn_others_are_light_cards()
    {
        var run1 = Guid.NewGuid();
        var run2 = Guid.NewGuid();
        var detail = Detail(anchor: null, Turn(1, run1), Turn(2, run2));

        var view = await Projector(detail, r => r == run2 ? new[] { Step("s1", "c1"), Step("s2", "c2") } : Array.Empty<JournalStep>())
            .ProjectAsync(detail.Id, focusRunId: null, Team, CancellationToken.None);

        view.ShouldNotBeNull();
        var focused = view!.Turns.Single(t => t.Focused);
        focused.TurnIndex.ShouldBe(2, "no focus given → the LAST turn is focused");
        focused.Steps.Count.ShouldBe(2, "the focused turn is walked into its steps");

        var collapsed = view.Turns.Single(t => !t.Focused);
        collapsed.TurnIndex.ShouldBe(1);
        collapsed.Steps.ShouldBeEmpty("a non-focused turn is a light card — no steps");
    }

    [Fact]
    public async Task Project_by_run_anchors_the_runs_turn()
    {
        var run1 = Guid.NewGuid();
        var detail = Detail(anchor: 1, Turn(1, run1), Turn(2, Guid.NewGuid()));

        var view = await Projector(detail, r => r == run1 ? new[] { Step("s1", "c1") } : Array.Empty<JournalStep>())
            .ProjectByRunAsync(run1, Team, CancellationToken.None);

        view.ShouldNotBeNull();
        view!.AnchorTurnIndex.ShouldBe(1, "entering by a run anchors that run's turn");
        view.Turns.Single(t => t.Focused).TurnIndex.ShouldBe(1);
    }

    [Fact]
    public async Task The_view_cursor_is_the_focused_turns_newest_step_cursor()
    {
        var run = Guid.NewGuid();
        var detail = Detail(anchor: null, Turn(1, run));

        var view = await Projector(detail, _ => new[] { Step("s1", "cursor-A"), Step("s2", "cursor-B") })
            .ProjectAsync(detail.Id, null, Team, CancellationToken.None);

        view!.Cursor.ShouldBe("cursor-B", "the view cursor is the newest step's cursor — the delta head the client echoes back");
    }

    [Fact]
    public async Task An_empty_focused_turn_yields_an_empty_view_cursor()
    {
        var detail = Detail(anchor: null, Turn(1, Guid.NewGuid()));

        var view = await Projector(detail, _ => Array.Empty<JournalStep>()).ProjectAsync(detail.Id, null, Team, CancellationToken.None);

        view!.Cursor.ShouldBe("", "no steps → no delta head yet");
    }

    [Fact]
    public async Task Carries_the_user_message_and_result_onto_the_turn()
    {
        var detail = Detail(anchor: null, Turn(1, Guid.NewGuid(), user: "fix the auth bug", result: "Fixed the refresh race."));

        var turn = (await Projector(detail).ProjectAsync(detail.Id, null, Team, CancellationToken.None))!.Turns.Single();

        turn.UserMessage.ShouldBe("fix the auth bug");
        turn.Summary.ShouldBe("Fixed the refresh race.");
    }

    [Fact]
    public async Task A_past_turn_without_a_result_has_no_summary()
    {
        var detail = Detail(anchor: 2,
            Turn(1, Guid.NewGuid(), result: null, status: WorkflowRunStatus.Failure),
            Turn(2, Guid.NewGuid()));

        var view = await Projector(detail).ProjectByRunAsync(Guid.NewGuid(), Team, CancellationToken.None);

        // The status-word fallback is gone: every turn is walked, so a turn with no recorded result shows no turn-level
        // summary — its walked steps carry the story, not a synthetic status word.
        view!.Turns.Single(t => t.TurnIndex == 1).Summary.ShouldBeNull("a rich turn with no recorded result has no synthetic summary");
    }

    [Fact]
    public async Task Focuses_a_prior_attempts_run_and_walks_that_attempt_not_the_latest()
    {
        // Parity with the room's attempt switcher: entering by a MIDDLE attempt's run id (neither the turn's root nor
        // its newest) resolves the right turn (TurnIndexOf's Attempts clause) AND focuses THAT attempt — its own run is
        // walked and its own status shown, not the newest attempt's.
        var a1 = Guid.NewGuid();
        var a2 = Guid.NewGuid();
        var a3 = Guid.NewGuid();
        var turn = RerunTurn(1, (a1, WorkflowRunStatus.Failure), (a2, WorkflowRunStatus.Cancelled), (a3, WorkflowRunStatus.Success));
        var detail = Detail(anchor: null, turn);

        var view = await Projector(detail, r => r == a2 ? new[] { Step("mid", "cursor-mid") } : new[] { Step("wrong", "cursor-wrong") })
            .ProjectAsync(detail.Id, focusRunId: a2, Team, CancellationToken.None);

        view.ShouldNotBeNull();
        view!.AnchorTurnIndex.ShouldBe(1, "the nested-attempt anchor resolved to its turn (TurnIndexOf's Attempts clause)");

        var focused = view.Turns.Single(t => t.Focused);
        focused.RunId.ShouldBe(a2, "the FOCUSED run is the anchored attempt, not the newest (a3)");
        focused.Status.ShouldBe(WorkflowRunStatus.Cancelled, "the anchored attempt's OWN status, not the latest's Success");
        focused.Steps.Single().Id.ShouldBe("mid", "the anchored attempt's run was walked, not the newest");
        focused.Summary.ShouldBeNull("a focused prior attempt shows no turn-level result (its steps carry the story)");
    }

    [Fact]
    public async Task Exposes_the_attempt_ladder_marking_the_focused_attempt()
    {
        // The pager + lineage data: a reran turn surfaces its whole ladder (number · status · run · SOURCE · fork node ·
        // latest), and — on the focused turn — the anchored attempt is flagged Focused so the frontend knows which it shows.
        var a1 = Guid.NewGuid();
        var a2 = Guid.NewGuid();
        var a3 = Guid.NewGuid();
        var turn = RerunTurn(1, (a1, WorkflowRunStatus.Failure), (a2, WorkflowRunStatus.Cancelled), (a3, WorkflowRunStatus.Success));
        var detail = Detail(anchor: null, turn);

        var view = await Projector(detail).ProjectAsync(detail.Id, focusRunId: a2, Team, CancellationToken.None);

        var attempts = view!.Turns.Single(t => t.Focused).Attempts;
        attempts.Select(a => a.AttemptNumber).ShouldBe(new[] { 1, 2, 3 }, "the whole ladder, in order");
        attempts.Select(a => a.Status).ShouldBe(new[] { WorkflowRunStatus.Failure, WorkflowRunStatus.Cancelled, WorkflowRunStatus.Success });
        attempts.Select(a => a.RunId).ShouldBe(new[] { a1, a2, a3 });
        attempts.Select(a => a.SourceType).ShouldBe(new[] { "manual", "rerun", "rerun" }, "the rerun source is carried per attempt");
        attempts.Select(a => a.RerunFromNodeId).ShouldBe(new[] { null, "integrate", "integrate" }, "the fork node (rerun-from-node) is carried — null for the original attempt");
        attempts.Select(a => a.Error).ShouldBe(new[] { "the tests timed out", null, null }, "the failed attempt's reason is carried (why it was reran); a non-failed attempt has none");
        attempts.Single(a => a.IsLatest).RunId.ShouldBe(a3, "the newest attempt is flagged latest");
        attempts.Single(a => a.Focused).RunId.ShouldBe(a2, "exactly the anchored attempt is focused");
        attempts.Count(a => a.Focused).ShouldBe(1);
    }

    [Fact]
    public async Task A_single_attempt_turn_exposes_no_ladder()
    {
        var detail = Detail(anchor: null, Turn(1, Guid.NewGuid()));

        var view = await Projector(detail).ProjectAsync(detail.Id, focusRunId: null, Team, CancellationToken.None);

        view!.Turns.Single(t => t.Focused).Attempts.ShouldBeEmpty("a turn never reran carries no ladder — the frontend shows no pager");
    }

    [Fact]
    public async Task A_non_anchored_turns_ladder_focuses_its_latest_attempt()
    {
        // Every turn is walked now, so a non-anchored reran turn shows its OWN latest attempt as the shown (focused) one
        // — parity with the room's attempt switcher, not a preview with nothing focused.
        var a1 = Guid.NewGuid();
        var a2 = Guid.NewGuid();
        var reran = RerunTurn(1, (a1, WorkflowRunStatus.Failure), (a2, WorkflowRunStatus.Success));
        var detail = Detail(anchor: null, reran, Turn(2, Guid.NewGuid()));

        // Session entry anchors the LAST turn (2); turn 1 is still fully walked and shows its own latest attempt.
        var view = await Projector(detail).ProjectAsync(detail.Id, focusRunId: null, Team, CancellationToken.None);

        var turn1 = view!.Turns.Single(t => t.TurnIndex == 1);
        turn1.Focused.ShouldBeFalse("only the anchored turn is the scroll anchor");
        turn1.Attempts.Select(a => a.AttemptNumber).ShouldBe(new[] { 1, 2 }, "the turn exposes its full ladder");
        turn1.Attempts.Single(a => a.Focused).AttemptNumber.ShouldBe(2, "its latest attempt is the shown one");
    }

    [Fact]
    public async Task A_null_walk_on_the_focused_turn_yields_empty_steps()
    {
        // The focused run could conflate to null (a foreign/absent run) even though the session resolved — the
        // `?? Array.Empty` guard must hold, never NPE.
        var detail = Detail(anchor: null, Turn(1, Guid.NewGuid()));

        var view = await Projector(detail, _ => null).ProjectAsync(detail.Id, null, Team, CancellationToken.None);

        view!.Turns.Single().Steps.ShouldBeEmpty("a null walk degrades to empty steps, not a crash");
        view.Cursor.ShouldBe("");
    }

    [Fact]
    public async Task Every_turn_is_walked()
    {
        var run1 = Guid.NewGuid();
        var run2 = Guid.NewGuid();
        var run3 = Guid.NewGuid();
        var detail = Detail(anchor: null, Turn(1, run1), Turn(2, run2), Turn(3, run3));

        var walked = new List<Guid>();
        var projector = new JournalProjector(new FakeSessions(detail), new FakeWalk(r => { walked.Add(r); return Array.Empty<JournalStep>(); }));

        await projector.ProjectAsync(detail.Id, focusRunId: null, Team, CancellationToken.None);

        // Every turn's run is walked — each turn's full journal is available on expand (one read per turn, in order).
        walked.ShouldBe(new[] { run1, run2, run3 });
    }

    [Fact]
    public async Task A_session_with_no_turns_projects_an_empty_journal()
    {
        var detail = Detail(anchor: null /* no turns */);

        var view = await Projector(detail).ProjectAsync(detail.Id, null, Team, CancellationToken.None);

        view.ShouldNotBeNull("an empty session still projects (it's the team's) — it just has no turns");
        view!.Turns.ShouldBeEmpty();
        view.Cursor.ShouldBe("", "no focused turn → no delta head");
        view.AnchorTurnIndex.ShouldBeNull();
    }

    [Fact]
    public async Task A_foreign_session_projects_to_null()
    {
        (await Projector(null).ProjectAsync(Guid.NewGuid(), null, Team, CancellationToken.None)).ShouldBeNull();
        (await Projector(null).ProjectByRunAsync(Guid.NewGuid(), Team, CancellationToken.None)).ShouldBeNull("a foreign / missing target is null, never a leak");
    }

    private sealed class FakeSessions : ISessionReadService
    {
        private readonly SessionDetail? _detail;
        public FakeSessions(SessionDetail? detail) => _detail = detail;
        public Task<SessionDetail?> GetDetailAsync(Guid sessionId, Guid teamId, CancellationToken cancellationToken) => Task.FromResult(_detail);
        public Task<SessionDetail?> GetByRunAsync(Guid runId, Guid teamId, CancellationToken cancellationToken) => Task.FromResult(_detail);
        public Task<SessionPage> ListAsync(Guid teamId, string? cursor, int limit, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class FakeWalk : IJournalWalk
    {
        private readonly Func<Guid, IReadOnlyList<JournalStep>?> _steps;
        public FakeWalk(Func<Guid, IReadOnlyList<JournalStep>?> steps) => _steps = steps;
        public Task<IReadOnlyList<JournalStep>?> WalkAsync(Guid runId, Guid teamId, CancellationToken cancellationToken) => Task.FromResult(_steps(runId));
    }
}
