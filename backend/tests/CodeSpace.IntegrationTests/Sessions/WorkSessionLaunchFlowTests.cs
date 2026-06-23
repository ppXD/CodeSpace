using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
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
///   (d) two launches open two DISTINCT sessions, each at turn 1 (S2 always opens a NEW thread — continuing an
///       existing one is a later slice);
///   (e) a rejected launch (cross-team repo) opens NO session — the reject lands before the session is staged.
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
