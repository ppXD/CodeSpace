using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Sessions;
using CodeSpace.Core.Services.Tasks;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.IntegrationTests.Infrastructure.Jobs;
using CodeSpace.IntegrationTests.Workflows.Infrastructure;
using CodeSpace.Messages.Commands.Tasks;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Enums;
using CodeSpace.Messages.Tasks;
using CodeSpace.Messages.Tasks.Effort;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace CodeSpace.IntegrationTests.Sessions;

/// <summary>
/// S2 — a manual task launch AUTO-OPENS a WorkSession (Kind=Task) and binds the started run to it as turn 1, proven
/// against the REAL <see cref="ITaskLaunchService"/> over real Postgres:
///   (a) launching opens a session (Kind=Task, Status=Open, team + actor stamped, title derived from the goal) and
///       the run carries SessionId = that session + SessionTurnIndex = 1; the result echoes the SessionId;
///   (b) the SAME holds through the mediator (the transactional command path) — session + run commit together;
///   (c) an over-long goal is truncated to the title column (robustness — a 500-char goal can't crash the insert);
///   (d) two launches open two DISTINCT sessions, each at turn 1 (no continue id ⇒ a fresh thread);
///   (e) a rejected launch (cross-team repo) opens NO session — the reject lands before the session is staged.
///
/// <para>S3 adds CONTINUE: a launch carrying an existing <c>SessionId</c> becomes that thread's NEXT top-level turn
/// (turn 2, 3, …) instead of opening a new session — only top-level turns bump the ordinal (an inherited null-turn
/// child / replay does not); a missing / cross-team / archived target is rejected (fail-closed, no leak).</para>
///
/// <para>Tier: high-fidelity Integration — real launch service (real seed registry + router + factory + the new
/// WorkSessionService) + real run starter over real Postgres. The runs are staged, not executed (the session
/// binding is established at launch); the full HTTP→engine→agent chain carrying the binding is the E2E tier.</para>
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public class WorkSessionLaunchFlowTests
{
    private readonly PostgresFixture _fixture;

    public WorkSessionLaunchFlowTests(PostgresFixture fixture) { _fixture = fixture; }

    [Fact]
    public async Task Launch_opens_a_task_session_and_binds_the_run_to_turn_one()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        using var _pauseExec = PauseAutoExecute();

        var result = await LaunchAsync(new TaskLaunchRequest
        {
            TeamId = teamId,
            ActorUserId = userId,
            SurfaceKind = TaskLaunchSurfaceKinds.Chat,
            TaskText = "Work on the auth refactor",
            RequestedEffort = TaskEffortModes.Quick,
            Autonomy = "Confined",
        });

        result.SessionId.ShouldNotBe(Guid.Empty, "the launch result echoes the opened session id");

        using var verify = _fixture.BeginScope();
        var db = verify.Resolve<CodeSpaceDbContext>();

        var session = await db.WorkSession.AsNoTracking().SingleAsync(s => s.Id == result.SessionId);
        session.TeamId.ShouldBe(teamId);
        session.Kind.ShouldBe(WorkSessionKind.Task, "a manual task launch opens a Task-kind thread");
        session.Status.ShouldBe(WorkSessionStatus.Open);
        session.Title.ShouldBe("Work on the auth refactor", "the thread title is derived from the launch goal");
        session.CreatedBy.ShouldBe(userId, "the launching actor is stamped as the session creator");

        var run = await db.WorkflowRun.AsNoTracking().SingleAsync(r => r.Id == result.RunId);
        run.SessionId.ShouldBe(result.SessionId, "the started run is bound to the opened session");
        run.SessionTurnIndex.ShouldBe(1, "the launch run is the session's first turn");
    }

    [Fact]
    public async Task Launch_via_mediator_commits_the_session_and_run_together()
    {
        // The production path: LaunchTaskCommand through the mediator (TransactionalBehavior wraps it), so the
        // staged session + run commit in ONE transaction. Proves the session is durable end-to-end through the
        // command pipeline, not only via a direct service call.
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        using var _pauseExec = PauseAutoExecute();

        LaunchTaskResult result;
        using (var scope = _fixture.BeginScopeAs(userId, teamId, Roles.Admin))
            result = await scope.Resolve<IMediator>().Send(new LaunchTaskCommand
            {
                TaskText = "Ship the session layer",
                Effort = TaskEffortModes.Quick,
                Autonomy = "Confined",
            }, CancellationToken.None);

        result.SessionId.ShouldNotBe(Guid.Empty);

        using var verify = _fixture.BeginScope();
        var db = verify.Resolve<CodeSpaceDbContext>();

        (await db.WorkSession.AsNoTracking().AnyAsync(s => s.Id == result.SessionId))
            .ShouldBeTrue("the command transaction committed the opened session");
        var run = await db.WorkflowRun.AsNoTracking().SingleAsync(r => r.Id == result.RunId);
        run.SessionId.ShouldBe(result.SessionId, "session + run committed atomically through the transactional command path");
        run.SessionTurnIndex.ShouldBe(1);
    }

    [Fact]
    public async Task A_long_goal_is_truncated_to_the_session_title_column()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        using var _pauseExec = PauseAutoExecute();

        var longGoal = new string('x', WorkSession.TitleMaxLength + 250);

        var result = await LaunchAsync(new TaskLaunchRequest
        {
            TeamId = teamId,
            ActorUserId = userId,
            SurfaceKind = TaskLaunchSurfaceKinds.Chat,
            TaskText = longGoal,
            RequestedEffort = TaskEffortModes.Quick,
            Autonomy = "Confined",
        });

        using var verify = _fixture.BeginScope();
        var db = verify.Resolve<CodeSpaceDbContext>();

        var session = await db.WorkSession.AsNoTracking().SingleAsync(s => s.Id == result.SessionId);
        session.Title.Length.ShouldBe(WorkSession.TitleMaxLength,
            "a goal longer than the title column MUST be truncated by the service so real Postgres accepts the insert");
    }

    [Fact]
    public async Task Rename_updates_the_title_sanitised_and_is_team_scoped()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var (otherTeam, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);

        var sessionId = Guid.CreateVersion7();
        using (var seed = _fixture.BeginScope())
        {
            var db = seed.Resolve<CodeSpaceDbContext>();
            db.WorkSession.Add(new WorkSession { Id = sessionId, TeamId = teamId, Title = "Original", Kind = WorkSessionKind.Task, Status = WorkSessionStatus.Open, LastActivityAt = DateTimeOffset.UtcNow, CreatedBy = userId, LastModifiedBy = userId });
            await db.SaveChangesAsync();
        }

        // A foreign team can't rename it — an indistinguishable not-found, and the title is untouched.
        using (var foreignScope = _fixture.BeginScope())
            (await foreignScope.Resolve<IWorkSessionService>().RenameAsync(sessionId, "Hacked", otherTeam, CancellationToken.None)).ShouldBeFalse("a cross-team rename is a no-op");

        // The owning team renames it; multiline free text is collapsed to a one-line title.
        using (var scope = _fixture.BeginScope())
            (await scope.Resolve<IWorkSessionService>().RenameAsync(sessionId, "  Rename   the\n session  ", teamId, CancellationToken.None)).ShouldBeTrue();

        using var verify = _fixture.BeginScope();
        var title = await verify.Resolve<CodeSpaceDbContext>().WorkSession.AsNoTracking().Where(s => s.Id == sessionId).Select(s => s.Title).SingleAsync();
        title.ShouldBe("Rename the session", "sanitised (whitespace collapsed) + only the owning team could change it");
    }

    [Fact]
    public async Task Two_launches_open_two_distinct_sessions_each_at_turn_one()
    {
        // S2 always opens a NEW thread per launch; continuing an existing session is a later slice. Two launches ⇒
        // two distinct sessions, and each run is its own session's turn 1 (never turn 2 of the first).
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        using var _pauseExec = PauseAutoExecute();

        var first = await LaunchAsync(BasicChatRequest(teamId, userId, "First task"));
        var second = await LaunchAsync(BasicChatRequest(teamId, userId, "Second task"));

        first.SessionId.ShouldNotBe(second.SessionId, "each manual launch opens its own thread");

        using var verify = _fixture.BeginScope();
        var db = verify.Resolve<CodeSpaceDbContext>();

        var firstRun = await db.WorkflowRun.AsNoTracking().SingleAsync(r => r.Id == first.RunId);
        var secondRun = await db.WorkflowRun.AsNoTracking().SingleAsync(r => r.Id == second.RunId);
        firstRun.SessionTurnIndex.ShouldBe(1);
        secondRun.SessionTurnIndex.ShouldBe(1, "the second launch is turn 1 of its OWN session, not turn 2 of the first");
        secondRun.SessionId.ShouldBe(second.SessionId);
    }

    [Fact]
    public async Task A_cross_team_repo_reject_opens_no_session()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var (otherTeamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var foreignRepoId = await SeedRepositoryAsync(otherTeamId);

        // The team-scope reject lands BEFORE the session is opened, so a rejected launch leaves no orphan thread.
        await Should.ThrowAsync<KeyNotFoundException>(() => LaunchAsync(new TaskLaunchRequest
        {
            TeamId = teamId,
            ActorUserId = userId,
            SurfaceKind = TaskLaunchSurfaceKinds.Chat,
            TaskText = "Try to reach a foreign repo",
            RepositoryId = foreignRepoId,
            RequestedEffort = TaskEffortModes.Standard,
        }));

        using var verify = _fixture.BeginScope();
        var db = verify.Resolve<CodeSpaceDbContext>();
        (await db.WorkSession.AsNoTracking().CountAsync(s => s.TeamId == teamId))
            .ShouldBe(0, "a rejected launch must open no session — the reject precedes the session open");
    }

    // ─── S3: continue an existing session ────────────────────────────────────────

    [Fact]
    public async Task Continue_binds_the_next_run_to_the_same_session_at_turn_two()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        using var _pauseExec = PauseAutoExecute();

        var first = await LaunchAsync(BasicChatRequest(teamId, userId, "Open the thread"));
        var second = await LaunchAsync(ContinueRequest(teamId, userId, first.SessionId, "Follow up"));

        second.SessionId.ShouldBe(first.SessionId, "a continue stays in the SAME session, not a new one");

        using var verify = _fixture.BeginScope();
        var db = verify.Resolve<CodeSpaceDbContext>();
        var secondRun = await db.WorkflowRun.AsNoTracking().SingleAsync(r => r.Id == second.RunId);
        secondRun.SessionId.ShouldBe(first.SessionId, "the follow-up run is bound to the original session");
        secondRun.SessionTurnIndex.ShouldBe(2, "the follow-up is the session's next top-level turn");

        (await db.WorkSession.AsNoTracking().CountAsync(s => s.TeamId == teamId))
            .ShouldBe(1, "continuing must NOT open a second session");
    }

    [Fact]
    public async Task Continue_increments_across_multiple_turns()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        using var _pauseExec = PauseAutoExecute();

        var t1 = await LaunchAsync(BasicChatRequest(teamId, userId, "Turn one"));
        var t2 = await LaunchAsync(ContinueRequest(teamId, userId, t1.SessionId, "Turn two"));
        var t3 = await LaunchAsync(ContinueRequest(teamId, userId, t1.SessionId, "Turn three"));

        (await TurnOf(t1.RunId)).ShouldBe(1);
        (await TurnOf(t2.RunId)).ShouldBe(2);
        (await TurnOf(t3.RunId)).ShouldBe(3, "each follow-up consumes the next ordinal");
    }

    [Fact]
    public async Task Concurrent_continues_to_the_same_session_get_distinct_turn_ordinals()
    {
        // Single-statement atomicity: the old MAX(turn)+1 read could hand two simultaneous continues the SAME ordinal.
        // The atomic counter (UPDATE … RETURNING row-locks the session) serialises them, so N concurrent continues get
        // the N DISTINCT ordinals 2..N+1 — never a duplicate. Postgres row-lock serialisation on `x = x + 1` is
        // identical in autocommit and in an explicit transaction, so resolving ContinueAsync in bare scopes proves the
        // distinctness without a launch transaction; the SEPARATE enlistment/rollback contract (a failed launch leaves
        // the counter unadvanced) is pinned by A_rolled_back_launch_does_not_advance_the_turn_counter below.
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var first = await LaunchAsync(BasicChatRequest(teamId, userId, "Open the thread"));
        var sessionId = first.SessionId;

        const int n = 8;
        var turns = await Task.WhenAll(Enumerable.Range(0, n).Select(async _ =>
        {
            using var scope = _fixture.BeginScope();
            var assignment = await scope.Resolve<IWorkSessionService>().ContinueAsync(sessionId, teamId, CancellationToken.None);
            return assignment.TurnIndex!.Value;
        }));

        turns.Distinct().Count().ShouldBe(n, "each concurrent continue must claim a DISTINCT ordinal — no MAX+1 duplicate");
        turns.OrderBy(t => t).ShouldBe(Enumerable.Range(2, n), "the claimed ordinals are the contiguous block 2..N+1 (turn 1 was the opening run)");
    }

    [Fact]
    public async Task A_rolled_back_launch_does_not_advance_the_turn_counter()
    {
        // The production atomicity contract: ContinueAsync's `UPDATE … RETURNING` enlists in the launch's AMBIENT
        // transaction (it runs on the same request-scoped DbContext connection), so a launch that FAILS after claiming
        // an ordinal must roll the increment back — the next successful continue REUSES that ordinal, never skips it.
        // Autocommit scopes can't prove this (the increment commits instantly); only an explicit transaction that rolls
        // back can. Without enlistment a claimed-then-failed launch would burn ordinal 2 and the retry would jump to 3.
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        using var _pauseExec = PauseAutoExecute();

        var first = await LaunchAsync(BasicChatRequest(teamId, userId, "Open the thread"));
        var sessionId = first.SessionId;

        using (var scope = _fixture.BeginScope())
        {
            var db = scope.Resolve<CodeSpaceDbContext>();
            await using var tx = await db.Database.BeginTransactionAsync();

            var claimed = await scope.Resolve<IWorkSessionService>().ContinueAsync(sessionId, teamId, CancellationToken.None);
            claimed.TurnIndex!.Value.ShouldBe(2, "the claim inside the transaction sees the next ordinal");

            await tx.RollbackAsync();   // the launch fails before commit — the increment must unwind with the transaction
        }

        var retry = await LaunchAsync(ContinueRequest(teamId, userId, sessionId, "Retry after the failure"));

        (await TurnOf(retry.RunId)).ShouldBe(2, "the rolled-back claim must NOT have advanced the counter — ordinal 2 is reused, not skipped to 3");
    }

    [Fact]
    public async Task A_child_run_with_a_null_turn_does_not_bump_the_next_turn()
    {
        // Correction 1: a child / replay inherits SessionId with a NULL turn index and must NOT count toward the
        // next top-level turn. After turn 1 + an inherited null-turn run, the next continue is still turn 2 (not 3).
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        using var _pauseExec = PauseAutoExecute();

        var first = await LaunchAsync(BasicChatRequest(teamId, userId, "Open"));
        await SeedInheritedNullTurnRunAsync(teamId, first.SessionId);

        var next = await LaunchAsync(ContinueRequest(teamId, userId, first.SessionId, "Follow up"));

        (await TurnOf(next.RunId)).ShouldBe(2,
            "an inherited null-turn run must not bump the ordinal — only top-level turns count");
    }

    [Fact]
    public async Task Continue_a_nonexistent_session_is_rejected_and_creates_no_run()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        using var _pauseExec = PauseAutoExecute();

        await Should.ThrowAsync<KeyNotFoundException>(() =>
            LaunchAsync(ContinueRequest(teamId, userId, Guid.NewGuid(), "Into the void")));

        using var verify = _fixture.BeginScope();
        var db = verify.Resolve<CodeSpaceDbContext>();
        (await db.WorkflowRun.AsNoTracking().CountAsync(r => r.TeamId == teamId))
            .ShouldBe(0, "a continue into a missing session must create no run");
    }

    [Fact]
    public async Task Continue_a_cross_team_session_is_not_found_and_never_leaked()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var (otherTeamId, otherUserId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        using var _pauseExec = PauseAutoExecute();

        // A real session owned by ANOTHER team.
        var foreign = await LaunchAsync(BasicChatRequest(otherTeamId, otherUserId, "Their thread"));

        var ex = await Should.ThrowAsync<KeyNotFoundException>(() =>
            LaunchAsync(ContinueRequest(teamId, userId, foreign.SessionId, "Try to reach it")));

        ex.Message.ShouldContain("not found or not accessible",
            customMessage: "a cross-team session must surface as a generic not-found — never reveal it exists in another team");
    }

    [Fact]
    public async Task Continue_an_archived_session_is_rejected()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        using var _pauseExec = PauseAutoExecute();

        var first = await LaunchAsync(BasicChatRequest(teamId, userId, "Open then retire"));
        await ArchiveSessionAsync(first.SessionId);

        await Should.ThrowAsync<InvalidOperationException>(() =>
            LaunchAsync(ContinueRequest(teamId, userId, first.SessionId, "Too late")));
    }

    [Fact]
    public async Task Continue_via_mediator_binds_the_next_turn()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        using var _pauseExec = PauseAutoExecute();

        var first = await LaunchAsync(BasicChatRequest(teamId, userId, "Open via service"));

        LaunchTaskResult second;
        using (var scope = _fixture.BeginScopeAs(userId, teamId, Roles.Admin))
            second = await scope.Resolve<IMediator>().Send(new LaunchTaskCommand
            {
                TaskText = "Continue via the command",
                SessionId = first.SessionId,
                Effort = TaskEffortModes.Quick,
                Autonomy = "Confined",
            }, CancellationToken.None);

        second.SessionId.ShouldBe(first.SessionId, "the command's SessionId continues the named thread");
        (await TurnOf(second.RunId)).ShouldBe(2);
    }

    // ─── Helpers ────────────────────────────────────────────────────────────────

    private static TaskLaunchRequest BasicChatRequest(Guid teamId, Guid userId, string text) => new()
    {
        TeamId = teamId,
        ActorUserId = userId,
        SurfaceKind = TaskLaunchSurfaceKinds.Chat,
        TaskText = text,
        RequestedEffort = TaskEffortModes.Quick,
        Autonomy = "Confined",
    };

    private static TaskLaunchRequest ContinueRequest(Guid teamId, Guid userId, Guid sessionId, string text) => new()
    {
        TeamId = teamId,
        ActorUserId = userId,
        SurfaceKind = TaskLaunchSurfaceKinds.Chat,
        TaskText = text,
        ContinueSessionId = sessionId,
        RequestedEffort = TaskEffortModes.Quick,
        Autonomy = "Confined",
    };

    private async Task<int?> TurnOf(Guid runId)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        return await db.WorkflowRun.AsNoTracking().Where(r => r.Id == runId).Select(r => r.SessionTurnIndex).SingleAsync();
    }

    /// <summary>Stage a run that INHERITS a session (SessionId set) but consumes NO turn (SessionTurnIndex null) — the child / replay shape, so the next-turn computation must skip it.</summary>
    private async Task SeedInheritedNullTurnRunAsync(Guid teamId, Guid sessionId)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        var requestId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;

        db.WorkflowRunRequest.Add(new WorkflowRunRequest
        {
            Id = requestId, TeamId = teamId, SourceType = WorkflowRunSourceTypes.Replay, ActorType = "user",
            ActorId = SystemUsers.SeederId, NormalizedPayloadJson = "{}", Status = WorkflowRunRequestStatus.Consumed,
            ReceivedAt = now, VerifiedAt = now, NormalizedAt = now,
        });
        db.WorkflowRun.Add(new WorkflowRun
        {
            Id = Guid.NewGuid(), TeamId = teamId, RunRequestId = requestId, SourceType = WorkflowRunSourceTypes.Replay,
            Status = WorkflowRunStatus.Pending, SessionId = sessionId, SessionTurnIndex = null,
            CreatedBy = SystemUsers.SeederId, LastModifiedBy = SystemUsers.SeederId,
        });

        await db.SaveChangesAsync();
    }

    private async Task ArchiveSessionAsync(Guid sessionId)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var session = await db.WorkSession.SingleAsync(s => s.Id == sessionId);
        session.Status = WorkSessionStatus.Archived;
        await db.SaveChangesAsync();
    }

    private async Task<LaunchTaskResult> LaunchAsync(TaskLaunchRequest request)
    {
        using var scope = _fixture.BeginScope();
        return await scope.Resolve<ITaskLaunchService>().LaunchAsync(request, CancellationToken.None);
    }

    /// <summary>
    /// Keep the launch's post-commit dispatch from auto-running the engine — these tests assert the STAGED
    /// session/run (the binding is written at stage time), not the run outcome. The shared
    /// <see cref="InMemoryBackgroundJobClient"/> is a fixture singleton the whole serial collection reuses and whose
    /// default <c>AutoExecute=true</c> sibling tests (e.g. the webhook-registration flow) depend on, so the pause is
    /// SCOPED: disposing it restores the default, leaving no cross-test pollution.
    /// </summary>
    private IDisposable PauseAutoExecute()
    {
        SetAutoExecute(clearFirst: true, value: false);
        return new Restore(this);
    }

    private void SetAutoExecute(bool clearFirst, bool value)
    {
        using var scope = _fixture.BeginScope();
        var jobClient = scope.Resolve<InMemoryBackgroundJobClient>();
        if (clearFirst) jobClient.Clear();
        jobClient.AutoExecute = value;
    }

    private sealed class Restore : IDisposable
    {
        private readonly WorkSessionLaunchFlowTests _owner;
        public Restore(WorkSessionLaunchFlowTests owner) { _owner = owner; }
        public void Dispose() => _owner.SetAutoExecute(clearFirst: false, value: true);   // restore the collection's default
    }

    /// <summary>Seeds a provider instance + an active repository in the given team — enough to satisfy (or, cross-team, fail) the launch service's team-scope check.</summary>
    private async Task<Guid> SeedRepositoryAsync(Guid teamId)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        var suffix = Guid.NewGuid().ToString("N")[..8];
        var instanceId = Guid.NewGuid();
        var repoId = Guid.NewGuid();

        db.ProviderInstance.Add(new ProviderInstance { Id = instanceId, TeamId = teamId, Provider = ProviderKind.GitHub, DisplayName = "GH", BaseUrl = $"https://gh-{suffix}.local", CreatedBy = SystemUsers.SeederId, LastModifiedBy = SystemUsers.SeederId });
        db.Repository.Add(new Repository { Id = repoId, TeamId = teamId, ProviderInstanceId = instanceId, ExternalId = $"ext-{suffix}", NamespacePath = "acme", Name = "api", FullPath = $"acme/api-{suffix}", WebUrl = "https://gh.local/acme/api", CreatedBy = SystemUsers.SeederId, LastModifiedBy = SystemUsers.SeederId });

        await db.SaveChangesAsync();
        return repoId;
    }
}
