using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Sessions;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.IntegrationTests.Workflows.Infrastructure;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Enums;
using Shouldly;

namespace CodeSpace.IntegrationTests.Sessions;

/// <summary>
/// The Sessions READ API (<see cref="ISessionReadService"/>) over real Postgres — the list + the conversation + the
/// run→session resolver, seeded by inserting the session / run rows directly (the read side is decoupled from how runs
/// are produced). Covers, end to end against a real DB: MRU ordering (a later-active OLD session outranks a newer
/// untouched one), keyset pagination, team isolation, the latest-run + pending-decision signals, turns oldest-first
/// with message / result / branch, rerun attempts NESTED under their turn (latest-wins), child-run exclusion, the
/// multi-repo branch shape, and the by-run anchor for both a turn run and one of its attempt runs.
///
/// <para>Tier: high-fidelity Integration — the real read service + EF translation over real Postgres (so the keyset
/// tuple comparison, the array/JSON reads, and the pending-decision joins are proven to translate + match real rows),
/// not a unit double.</para>
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public class SessionReadFlowTests
{
    private readonly PostgresFixture _fixture;

    public SessionReadFlowTests(PostgresFixture fixture) { _fixture = fixture; }

    // ─── List ─────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Lists_sessions_most_recently_active_first_even_when_an_older_session_was_touched_later()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var now = DateTimeOffset.UtcNow;

        // 'old' was CREATED first but ACTIVE most recently (a continued thread); 'newer' was created later but untouched.
        var older = await SeedSessionAsync(teamId, userId, "Old thread, just continued", lastActivityAt: now, turns: 3);
        var newer = await SeedSessionAsync(teamId, userId, "Newer thread, idle", lastActivityAt: now.AddHours(-2), turns: 1);

        var page = await ListAsync(teamId);

        page.Items.Select(s => s.Id).ShouldBe(new[] { older, newer }, "MRU: the most-recently-active thread is first, not the most-recently-created");
        var top = page.Items[0];
        top.Title.ShouldBe("Old thread, just continued");
        top.TurnCount.ShouldBe(3, "TurnCount is the session's atomic turn counter");
        top.Kind.ShouldBe(WorkSessionKind.Task);
        top.Status.ShouldBe(WorkSessionStatus.Open);
    }

    [Fact]
    public async Task List_excludes_other_teams_sessions()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var (otherTeam, otherUser) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);

        var mine = await SeedSessionAsync(teamId, userId, "Mine", DateTimeOffset.UtcNow, turns: 1);
        await SeedSessionAsync(otherTeam, otherUser, "Theirs", DateTimeOffset.UtcNow, turns: 1);

        var page = await ListAsync(teamId);

        page.Items.Select(s => s.Id).ShouldBe(new[] { mine }, "only the caller's team's sessions are listed");
    }

    [Fact]
    public async Task List_surfaces_the_latest_run_and_the_pending_decision_badge()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var now = DateTimeOffset.UtcNow;

        var sessionId = await SeedSessionAsync(teamId, userId, "Live work", lastActivityAt: now, turns: 2);
        await SeedRunAsync(teamId, sessionId, turnIndex: 1, status: WorkflowRunStatus.Success, createdAt: now.AddMinutes(-5));
        var latest = await SeedRunAsync(teamId, sessionId, turnIndex: 2, status: WorkflowRunStatus.Running, projectionKind: "supervisor", createdAt: now);
        await SeedPendingDecisionAsync(latest);

        var row = (await ListAsync(teamId)).Items.ShouldHaveSingleItem();

        row.LatestRunId.ShouldBe(latest, "the most-recent run drives the badge + deep-link");
        row.LatestRunStatus.ShouldBe(WorkflowRunStatus.Running);
        row.LatestProjectionKind.ShouldBe("supervisor");
        row.HasPendingDecision.ShouldBeTrue("a real pending Decision wait lights the needs-you badge");
    }

    [Fact]
    public async Task List_keyset_pages_without_overlap_or_gaps()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var now = DateTimeOffset.UtcNow;

        var ids = new List<Guid>();
        for (var i = 0; i < 5; i++)
            ids.Add(await SeedSessionAsync(teamId, userId, $"S{i}", lastActivityAt: now.AddMinutes(-i), turns: 1));
        // ids[0] is most-recent → the expected DESC order is ids as-is.

        var seen = new List<Guid>();
        string? cursor = null;
        var pages = 0;
        do
        {
            var page = await ListAsync(teamId, cursor, limit: 2);
            seen.AddRange(page.Items.Select(s => s.Id));
            cursor = page.NextCursor;
            pages++;
            pages.ShouldBeLessThanOrEqualTo(5, "pagination must terminate");
        }
        while (cursor != null);

        seen.ShouldBe(ids, "every session is returned exactly once, in MRU order, across the keyset pages");
        seen.Distinct().Count().ShouldBe(5, "no row is repeated across pages");
    }

    [Fact]
    public async Task List_keyset_pages_cleanly_when_sessions_share_an_activity_instant()
    {
        // The id tiebreaker: when rows share a last_activity_at, the keyset boundary leans on the == arm
        // (s.Id.CompareTo(c.Id), translated to Postgres uuid order, matching ORDER BY id DESC). This proves that arm
        // against real Postgres — a C#-vs-uuid order disagreement would silently drop or duplicate a row on a tie.
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var tie = new DateTimeOffset(2026, 6, 28, 12, 0, 0, TimeSpan.Zero);   // identical for all → forces the tiebreaker

        var ids = new HashSet<Guid>();
        for (var i = 0; i < 3; i++)
            ids.Add(await SeedSessionAsync(teamId, userId, $"Tie{i}", lastActivityAt: tie, turns: 1));

        var seen = new List<Guid>();
        string? cursor = null;
        var pages = 0;
        do
        {
            var page = await ListAsync(teamId, cursor, limit: 1);   // one-per-page → every boundary is a tie boundary
            seen.AddRange(page.Items.Select(s => s.Id));
            cursor = page.NextCursor;
            pages++;
            pages.ShouldBeLessThanOrEqualTo(4, "pagination must terminate even across a full tie");
        }
        while (cursor != null);

        seen.Count.ShouldBe(3, "every tied session is returned exactly once across the keyset pages");
        seen.ToHashSet().SetEquals(ids).ShouldBeTrue("no tied row is dropped or duplicated at a page boundary (the id tiebreaker)");
    }

    [Fact]
    public async Task Continuing_a_session_bumps_its_activity_so_it_jumps_to_the_top()
    {
        // Proves the ContinueAsync `last_activity_at = now()` bump end-to-end through the read path: an older-active
        // thread, once continued, outranks a more-recently-active sibling in the MRU list.
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var now = DateTimeOffset.UtcNow;

        var stale = await SeedSessionAsync(teamId, userId, "Stale then continued", lastActivityAt: now.AddHours(-2), turns: 1);
        await SeedSessionAsync(teamId, userId, "More recently active", lastActivityAt: now.AddHours(-1), turns: 1);

        using (var scope = _fixture.BeginScope())
            await scope.Resolve<IWorkSessionService>().ContinueAsync(stale, teamId, CancellationToken.None);

        (await ListAsync(teamId)).Items[0].Id.ShouldBe(stale, "continuing bumps last_activity_at to now() → the thread jumps to the top of the MRU list");
    }

    // ─── Detail ───────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Detail_returns_turns_oldest_first_with_message_result_and_branch()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var now = DateTimeOffset.UtcNow;
        var sessionId = await SeedSessionAsync(teamId, userId, "Build the feature", lastActivityAt: now, turns: 2);

        await SeedRunAsync(teamId, sessionId, turnIndex: 1, goal: "Add login", status: WorkflowRunStatus.Success,
            outputs: """{"summary":"added login","branch":"cs/login"}""", createdAt: now.AddMinutes(-10));
        await SeedRunAsync(teamId, sessionId, turnIndex: 2, goal: "Now add logout", status: WorkflowRunStatus.Running,
            createdAt: now);

        var detail = await DetailAsync(sessionId, teamId);

        detail.ShouldNotBeNull();
        detail!.Turns.Select(t => t.TurnIndex).ShouldBe(new[] { 1, 2 }, "turns oldest-first");
        detail.Turns[0].UserMessage.ShouldBe("Add login", "the user message is the run goal");
        detail.Turns[0].Result.ShouldBe("added login");
        detail.Turns[0].ProducedBranch.ShouldBe("cs/login");
        detail.Turns[0].AttemptCount.ShouldBe(1);
        detail.Turns[1].UserMessage.ShouldBe("Now add logout");
        detail.Turns[1].RunStatus.ShouldBe(WorkflowRunStatus.Running);
    }

    [Fact]
    public async Task Detail_nests_rerun_attempts_under_their_turn_with_latest_wins()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var now = DateTimeOffset.UtcNow;
        var sessionId = await SeedSessionAsync(teamId, userId, "Flaky turn", lastActivityAt: now, turns: 1);

        var original = await SeedRunAsync(teamId, sessionId, turnIndex: 1, goal: "Do the thing", status: WorkflowRunStatus.Failure,
            outputs: """{"summary":"boom"}""", createdAt: now.AddMinutes(-10));
        await SeedRunAsync(teamId, sessionId, turnIndex: null, rootRunId: original, status: WorkflowRunStatus.Failure,
            source: WorkflowRunSourceTypes.Replay, createdAt: now.AddMinutes(-5));
        var winner = await SeedRunAsync(teamId, sessionId, turnIndex: null, rootRunId: original, status: WorkflowRunStatus.Success,
            source: WorkflowRunSourceTypes.Rerun, rerunFromNodeId: "impl", outputs: """{"summary":"fixed","branch":"cs/fixed"}""", createdAt: now);

        var detail = await DetailAsync(sessionId, teamId);

        var turn = detail!.Turns.ShouldHaveSingleItem();
        turn.TurnRunId.ShouldBe(original, "the turn's identity is the original run");
        turn.RunId.ShouldBe(winner, "the turn shows its newest attempt");
        turn.RunStatus.ShouldBe(WorkflowRunStatus.Success, "latest-wins");
        turn.Result.ShouldBe("fixed");
        turn.ProducedBranch.ShouldBe("cs/fixed");
        turn.AttemptCount.ShouldBe(3);
        turn.Attempts!.Select(a => a.RunId).ShouldBe(new[] { original, turn.Attempts[1].RunId, winner }, "oldest → newest");
        turn.Attempts[^1].IsLatest.ShouldBeTrue();
        turn.Attempts[^1].RerunFromNodeId.ShouldBe("impl");
    }

    [Fact]
    public async Task Detail_excludes_nested_sub_workflow_child_runs()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var now = DateTimeOffset.UtcNow;
        var sessionId = await SeedSessionAsync(teamId, userId, "Has a child", lastActivityAt: now, turns: 1);

        await SeedRunAsync(teamId, sessionId, turnIndex: 1, status: WorkflowRunStatus.Success, createdAt: now);
        // A sub-workflow child inherits the SessionId but must never appear as a turn / attempt.
        await SeedRunAsync(teamId, sessionId, turnIndex: null, source: WorkflowRunSourceTypes.ChildWorkflow, status: WorkflowRunStatus.Success, createdAt: now.AddMinutes(1));

        var detail = await DetailAsync(sessionId, teamId);

        detail!.Turns.ShouldHaveSingleItem().AttemptCount.ShouldBe(1, "the child run is not a turn nor an attempt");
    }

    [Fact]
    public async Task Detail_surfaces_per_turn_pending_decision_and_multi_repo_branches()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var now = DateTimeOffset.UtcNow;
        var repoA = Guid.NewGuid(); var repoB = Guid.NewGuid();
        var sessionId = await SeedSessionAsync(teamId, userId, "Multi-repo + parked", lastActivityAt: now, turns: 2);

        var parked = await SeedRunAsync(teamId, sessionId, turnIndex: 1, status: WorkflowRunStatus.Suspended, createdAt: now.AddMinutes(-5));
        await SeedPendingDecisionAsync(parked);
        await SeedRunAsync(teamId, sessionId, turnIndex: 2, status: WorkflowRunStatus.Success, createdAt: now,
            outputs: $$"""{"repositoryResults":[{"repositoryId":"{{repoA}}","producedBranch":"cs/a"},{"repositoryId":"{{repoB}}","producedBranch":"cs/b"}]}""");

        var detail = await DetailAsync(sessionId, teamId);

        detail!.Turns[0].HasPendingDecision.ShouldBeTrue("turn 1 is parked on a real decision");
        detail.Turns[1].HasPendingDecision.ShouldBeFalse();
        detail.Turns[1].ProducedBranch.ShouldBeNull("a multi-repo turn has no single flat branch");
        detail.Turns[1].RepositoryResults!.Count.ShouldBe(2);
        detail.Turns[1].RepositoryResults!.ShouldContain(r => r.RepositoryId == repoA && r.ProducedBranch == "cs/a");
    }

    [Fact]
    public async Task Detail_of_a_foreign_session_is_not_found()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var (otherTeam, otherUser) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var foreign = await SeedSessionAsync(otherTeam, otherUser, "Theirs", DateTimeOffset.UtcNow, turns: 1);

        (await DetailAsync(foreign, teamId)).ShouldBeNull("a cross-team session is an indistinguishable not-found — never a leak");
    }

    // ─── By run ───────────────────────────────────────────────────────────────────

    [Fact]
    public async Task By_run_resolves_a_turn_run_to_its_thread_anchored_at_that_turn()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var now = DateTimeOffset.UtcNow;
        var sessionId = await SeedSessionAsync(teamId, userId, "Thread", lastActivityAt: now, turns: 2);
        await SeedRunAsync(teamId, sessionId, turnIndex: 1, status: WorkflowRunStatus.Success, createdAt: now.AddMinutes(-5));
        var turn2 = await SeedRunAsync(teamId, sessionId, turnIndex: 2, status: WorkflowRunStatus.Running, createdAt: now);

        var detail = await ByRunAsync(turn2, teamId);

        detail.ShouldNotBeNull();
        detail!.Id.ShouldBe(sessionId, "the run resolves to its owning thread");
        detail.AnchorTurnIndex.ShouldBe(2, "anchored at the run's own turn");
        detail.Turns.Count.ShouldBe(2, "the WHOLE thread is returned, not just the anchored turn");
    }

    [Fact]
    public async Task By_run_resolves_an_attempt_run_to_the_owning_turn()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var now = DateTimeOffset.UtcNow;
        var sessionId = await SeedSessionAsync(teamId, userId, "Reran", lastActivityAt: now, turns: 1);
        var original = await SeedRunAsync(teamId, sessionId, turnIndex: 1, status: WorkflowRunStatus.Failure, createdAt: now.AddMinutes(-5));
        var attempt = await SeedRunAsync(teamId, sessionId, turnIndex: null, rootRunId: original, status: WorkflowRunStatus.Success,
            source: WorkflowRunSourceTypes.Rerun, createdAt: now);

        var detail = await ByRunAsync(attempt, teamId);

        detail.ShouldNotBeNull();
        detail!.Id.ShouldBe(sessionId);
        detail.AnchorTurnIndex.ShouldBe(1, "an attempt run anchors at the turn it belongs to, not its own (null) turn");
    }

    [Fact]
    public async Task By_run_is_not_found_for_a_session_less_or_foreign_run()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var now = DateTimeOffset.UtcNow;

        // A run with no session.
        var sessionLess = await SeedRunAsync(teamId, sessionId: null, turnIndex: null, status: WorkflowRunStatus.Success, createdAt: now);
        (await ByRunAsync(sessionLess, teamId)).ShouldBeNull("a session-less run resolves to no thread");

        // A run owned by another team.
        var (otherTeam, otherUser) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var foreignSession = await SeedSessionAsync(otherTeam, otherUser, "Theirs", now, turns: 1);
        var foreignRun = await SeedRunAsync(otherTeam, foreignSession, turnIndex: 1, status: WorkflowRunStatus.Success, createdAt: now);
        (await ByRunAsync(foreignRun, teamId)).ShouldBeNull("a cross-team run is not-found — never a leak");
    }

    // ─── Service entry points ───────────────────────────────────────────────────────

    private async Task<Messages.Dtos.Sessions.SessionPage> ListAsync(Guid teamId, string? cursor = null, int limit = 30)
    {
        using var scope = _fixture.BeginScope();
        return await scope.Resolve<ISessionReadService>().ListAsync(teamId, cursor, limit, CancellationToken.None);
    }

    private async Task<Messages.Dtos.Sessions.SessionDetail?> DetailAsync(Guid sessionId, Guid teamId)
    {
        using var scope = _fixture.BeginScope();
        return await scope.Resolve<ISessionReadService>().GetDetailAsync(sessionId, teamId, CancellationToken.None);
    }

    private async Task<Messages.Dtos.Sessions.SessionDetail?> ByRunAsync(Guid runId, Guid teamId)
    {
        using var scope = _fixture.BeginScope();
        return await scope.Resolve<ISessionReadService>().GetByRunAsync(runId, teamId, CancellationToken.None);
    }

    // ─── Seed helpers ───────────────────────────────────────────────────────────────

    private async Task<Guid> SeedSessionAsync(Guid teamId, Guid userId, string title, DateTimeOffset lastActivityAt, int turns)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        var id = Guid.CreateVersion7();
        db.WorkSession.Add(new WorkSession
        {
            Id = id, TeamId = teamId, Title = title, Kind = WorkSessionKind.Task, Status = WorkSessionStatus.Open,
            LastTurnIndex = turns, LastActivityAt = lastActivityAt, CreatedBy = userId, LastModifiedBy = userId,
        });
        await db.SaveChangesAsync();
        return id;
    }

    private async Task<Guid> SeedRunAsync(
        Guid teamId, Guid? sessionId, int? turnIndex, WorkflowRunStatus status, DateTimeOffset createdAt,
        string? goal = null, string outputs = "{}", string source = WorkflowRunSourceTypes.Manual,
        Guid? rootRunId = null, string? rerunFromNodeId = null, string? projectionKind = "single-agent")
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        var requestId = Guid.NewGuid();
        var runId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var payload = goal == null ? "{}" : System.Text.Json.JsonSerializer.Serialize(new { goal });

        db.WorkflowRunRequest.Add(new WorkflowRunRequest
        {
            Id = requestId, TeamId = teamId, SourceType = source, ActorType = "user", ActorId = SystemUsers.SeederId,
            NormalizedPayloadJson = payload, Status = WorkflowRunRequestStatus.Consumed,
            ReceivedAt = now, VerifiedAt = now, NormalizedAt = now,
        });
        db.WorkflowRun.Add(new WorkflowRun
        {
            Id = runId, TeamId = teamId, RunRequestId = requestId, SourceType = source, Status = status,
            SessionId = sessionId, SessionTurnIndex = turnIndex, RootRunId = rootRunId, RerunFromNodeId = rerunFromNodeId,
            ProjectionKind = projectionKind, OutputsJson = outputs, CreatedDate = createdAt,
            CreatedBy = SystemUsers.SeederId, LastModifiedBy = SystemUsers.SeederId,
        });
        await db.SaveChangesAsync();
        return runId;
    }

    private async Task SeedPendingDecisionAsync(Guid runId)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        db.WorkflowRunWait.Add(new WorkflowRunWait
        {
            Id = Guid.NewGuid(), RunId = runId, NodeId = "decide", WaitKind = WorkflowWaitKinds.Decision,
            Token = Guid.NewGuid().ToString("N"), Status = WorkflowWaitStatuses.Pending, CreatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();
    }
}
