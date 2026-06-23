using System.Text.Json;
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
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace CodeSpace.IntegrationTests.Sessions;

/// <summary>
/// S4b — branch continuity: a CONTINUE clones the primary repo at the prior turn's PRODUCED branch, so the agent
/// builds on earlier CODE (not just the narrative). Proven against real Postgres through the REAL
/// <see cref="ITaskLaunchService"/> (the resolved branch lands on the new run's frozen agent-code <c>baseRef</c>
/// input; the workspace provider's clone-at-ref is proven separately by <c>LocalGitWorkspaceProviderTests</c>):
///   (a) a continue inherits the prior turn's branch; (b) the MOST RECENT produced branch wins, skipping a later
///   analysis-only turn; (c) no prior branch ⇒ no baseRef ⇒ the repo's default branch (safe fallback); (d) a
///   different repo never inherits another repo's branch; (e) a fresh launch carries no baseRef (byte-identical).
///
/// <para>Tier: high-fidelity Integration — real launch service + branch resolver over real Postgres; runs are
/// staged, not executed (the binding is established at launch). v1 is single-repo (the run's <c>branch</c> output);
/// per-repo continuity for a multi-repo run is the noted follow-on.</para>
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public class WorkSessionBranchFlowTests
{
    private readonly PostgresFixture _fixture;

    public WorkSessionBranchFlowTests(PostgresFixture fixture) { _fixture = fixture; }

    [Fact]
    public async Task Continue_clones_the_primary_repo_at_the_prior_turns_branch()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        using var _pauseExec = PauseAutoExecute();
        var repoId = await SeedRepositoryAsync(teamId);
        var sessionId = await SeedSessionAsync(teamId);
        await SeedCodeTurnAsync(teamId, sessionId, turn: 1, repoId, branch: "run-1/x");

        var result = await LaunchAsync(ContinueRequest(teamId, userId, sessionId, repoId, "Add tests on top"));

        (await ReadAgentBaseRefAsync(result.RunId)).ShouldBe("run-1/x",
            "the continuing run clones the primary repo at the prior turn's produced branch — code carries forward");
    }

    [Fact]
    public async Task Continue_uses_the_most_recent_produced_branch_skipping_a_later_analysis_turn()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        using var _pauseExec = PauseAutoExecute();
        var repoId = await SeedRepositoryAsync(teamId);
        var sessionId = await SeedSessionAsync(teamId);
        await SeedCodeTurnAsync(teamId, sessionId, 1, repoId, "run-1/x");
        await SeedCodeTurnAsync(teamId, sessionId, 2, repoId, "run-2/x");
        await SeedCodeTurnAsync(teamId, sessionId, 3, repoId, branch: null);   // a later analysis-only turn changed no code

        var result = await LaunchAsync(ContinueRequest(teamId, userId, sessionId, repoId, "Keep going"));

        (await ReadAgentBaseRefAsync(result.RunId)).ShouldBe("run-2/x",
            "the latest code state is turn 2's branch (turn 3 produced none) — not turn 1, not base");
    }

    [Fact]
    public async Task Continue_with_no_prior_branch_clones_the_default_branch()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        using var _pauseExec = PauseAutoExecute();
        var repoId = await SeedRepositoryAsync(teamId);
        var sessionId = await SeedSessionAsync(teamId);
        await SeedCodeTurnAsync(teamId, sessionId, 1, repoId, branch: null);   // analysis-only prior turn

        var result = await LaunchAsync(ContinueRequest(teamId, userId, sessionId, repoId, "Now do the work"));

        (await ReadAgentBaseRefAsync(result.RunId)).ShouldBeNull(
            "no prior turn produced a branch → no baseRef → the repo's default branch (the safe fallback)");
    }

    [Fact]
    public async Task Continue_targeting_a_different_repo_does_not_inherit_a_branch()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        using var _pauseExec = PauseAutoExecute();
        var repoA = await SeedRepositoryAsync(teamId);
        var repoB = await SeedRepositoryAsync(teamId);
        var sessionId = await SeedSessionAsync(teamId);
        await SeedCodeTurnAsync(teamId, sessionId, 1, repoA, "run-1/a");

        var result = await LaunchAsync(ContinueRequest(teamId, userId, sessionId, repoB, "Work on a different repo"));

        (await ReadAgentBaseRefAsync(result.RunId)).ShouldBeNull(
            "repo B has no prior branch in this session — repo A's branch must never bleed across repos");
    }

    [Fact]
    public async Task Continue_does_not_inherit_a_branch_from_a_multi_repo_prior_turn()
    {
        // v1 trusts ONLY a single-repo turn's branch: a multi-repo turn's OutputsJson.branch is the PRIMARY's, so
        // attributing it to a repo could clone the wrong branch. Such a turn is skipped → safe fallback to the default
        // branch (per-repo continuity from RepositoryResults is the noted follow-on).
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        using var _pauseExec = PauseAutoExecute();
        var repoA = await SeedRepositoryAsync(teamId);
        var repoB = await SeedRepositoryAsync(teamId);
        var sessionId = await SeedSessionAsync(teamId);
        await SeedTurnAsync(teamId, sessionId, 1, new[] { repoA, repoB }, branch: "run-1/primary");

        var result = await LaunchAsync(ContinueRequest(teamId, userId, sessionId, repoA, "Continue"));

        (await ReadAgentBaseRefAsync(result.RunId)).ShouldBeNull(
            "a multi-repo prior turn's primary branch must NOT be attributed to a repo — skip it, fall back to base");
    }

    [Fact]
    public async Task A_fresh_launch_has_no_base_ref()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        using var _pauseExec = PauseAutoExecute();
        var repoId = await SeedRepositoryAsync(teamId);

        var result = await LaunchAsync(new TaskLaunchRequest
        {
            TeamId = teamId, ActorUserId = userId, SurfaceKind = TaskLaunchSurfaceKinds.Chat,
            TaskText = "Fresh start", RepositoryId = repoId, RequestedEffort = TaskEffortModes.Quick, Autonomy = "Confined",
        });

        (await ReadAgentBaseRefAsync(result.RunId)).ShouldBeNull("a fresh launch (no continue) clones the default branch — byte-identical");
    }

    [Fact]
    public async Task Branch_resolver_returns_the_most_recent_branch_for_the_repo()
    {
        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var repoId = await SeedRepositoryAsync(teamId);
        var sessionId = await SeedSessionAsync(teamId);
        await SeedCodeTurnAsync(teamId, sessionId, 1, repoId, "run-1/x");
        await SeedCodeTurnAsync(teamId, sessionId, 2, repoId, "run-2/x");

        using var scope = _fixture.BeginScope();
        var resolved = await scope.Resolve<ISessionBranchResolver>().ResolveStartRefAsync(sessionId, teamId, repoId, CancellationToken.None);

        resolved.ShouldBe("run-2/x", "the resolver returns the newest produced branch for the repo");
    }

    // ─── Helpers ────────────────────────────────────────────────────────────────

    private static TaskLaunchRequest ContinueRequest(Guid teamId, Guid userId, Guid sessionId, Guid repoId, string text) => new()
    {
        TeamId = teamId, ActorUserId = userId, SurfaceKind = TaskLaunchSurfaceKinds.Chat,
        TaskText = text, ContinueSessionId = sessionId, RepositoryId = repoId,
        RequestedEffort = TaskEffortModes.Quick, Autonomy = "Confined",
    };

    private async Task<LaunchTaskResult> LaunchAsync(TaskLaunchRequest request)
    {
        using var scope = _fixture.BeginScope();
        return await scope.Resolve<ITaskLaunchService>().LaunchAsync(request, CancellationToken.None);
    }

    /// <summary>Reads the projected agent.code node's <c>baseRef</c> input out of the frozen definition snapshot (null when absent ⇒ default branch).</summary>
    private async Task<string?> ReadAgentBaseRefAsync(Guid runId)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var run = await db.WorkflowRun.AsNoTracking().SingleAsync(r => r.Id == runId);

        var root = JsonDocument.Parse(run.DefinitionSnapshotJson!).RootElement;
        var agent = root.GetProperty("nodes").EnumerateArray().Single(n => n.GetProperty("id").GetString() == "agent");

        return agent.GetProperty("inputs").TryGetProperty("baseRef", out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    }

    private async Task<Guid> SeedSessionAsync(Guid teamId)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var id = Guid.CreateVersion7();
        db.WorkSession.Add(new WorkSession { Id = id, TeamId = teamId, Title = "thread", Kind = WorkSessionKind.Task, Status = WorkSessionStatus.Open });
        await db.SaveChangesAsync();
        return id;
    }

    /// <summary>Stage a finished single-repo turn that targeted <paramref name="repoId"/> and (optionally) produced a branch — the shape a real single-agent code turn leaves (scope repo + OutputsJson.branch).</summary>
    private Task SeedCodeTurnAsync(Guid teamId, Guid sessionId, int turn, Guid repoId, string? branch) =>
        SeedTurnAsync(teamId, sessionId, turn, new[] { repoId }, branch);

    /// <summary>Stage a finished turn over an arbitrary repo scope (single- or multi-repo) with an optional produced branch.</summary>
    private async Task SeedTurnAsync(Guid teamId, Guid sessionId, int turn, IReadOnlyList<Guid> scope, string? branch)
    {
        using var dbScope = _fixture.BeginScope();
        var db = dbScope.Resolve<CodeSpaceDbContext>();

        var requestId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;

        db.WorkflowRunRequest.Add(new WorkflowRunRequest
        {
            Id = requestId, TeamId = teamId, SourceType = WorkflowRunSourceTypes.Snapshot, ActorType = "user",
            ActorId = SystemUsers.SeederId, NormalizedPayloadJson = "{}", Status = WorkflowRunRequestStatus.Consumed,
            ReceivedAt = now, VerifiedAt = now, NormalizedAt = now,
        });
        db.WorkflowRun.Add(new WorkflowRun
        {
            Id = Guid.NewGuid(), TeamId = teamId, RunRequestId = requestId, SourceType = WorkflowRunSourceTypes.Snapshot,
            Status = WorkflowRunStatus.Success, SessionId = sessionId, SessionTurnIndex = turn,
            ScopeRepositoryIds = scope.ToList(),
            OutputsJson = branch is null ? "{}" : JsonSerializer.Serialize(new { branch }),
            CreatedBy = SystemUsers.SeederId, LastModifiedBy = SystemUsers.SeederId,
        });

        await db.SaveChangesAsync();
    }

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
        private readonly WorkSessionBranchFlowTests _owner;
        public Restore(WorkSessionBranchFlowTests owner) { _owner = owner; }
        public void Dispose() => _owner.SetAutoExecute(clearFirst: false, value: true);
    }
}
