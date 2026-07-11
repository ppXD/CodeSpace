using System.Text.Json;
using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Agents.Sandbox;
using CodeSpace.Core.Services.Agents.Sandbox.Runners;
using CodeSpace.Core.Services.Agents.Workspace;
using CodeSpace.Core.Services.Tasks;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.IntegrationTests.Infrastructure.Jobs;
using CodeSpace.IntegrationTests.Workflows.Infrastructure;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Commands.Tasks;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Enums;
using CodeSpace.Messages.Tasks;
using CodeSpace.Messages.Tasks.Effort;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace CodeSpace.IntegrationTests.Workflows;

/// <summary>
/// S1 — the launch's immutable base vector, proven through the REAL <see cref="ITaskLaunchService"/> over real
/// Postgres + a REAL local git remote: at launch each cloneable repo's tip is resolved ONCE over the git transport
/// and frozen as the agent node's <c>pinnedSha</c> (primary) / <c>relatedRepositories[].pinnedSha</c> (related), so
/// every participant of the run materializes the SAME base even when the remote advances after launch. URL-less and
/// session-continuing repos stay unpinned (byte-identical legacy); a HARD ref that is gone fails the launch loud.
///
/// <para>Tier: high-fidelity Integration — real launch pipeline, real git remote (<c>file://</c>); runs are staged,
/// not executed. The provider's materialize-at-pin is proven separately by <c>LocalGitWorkspaceProviderTests</c>.</para>
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public class LaunchBasePinFlowTests
{
    private readonly PostgresFixture _fixture;

    public LaunchBasePinFlowTests(PostgresFixture fixture) { _fixture = fixture; }

    [Fact]
    public async Task A_fresh_launch_freezes_the_remote_tip_as_the_primary_pin_even_when_the_tip_advances()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        using var _pauseExec = PauseAutoExecute();

        using var remote = new GitRemote();
        var tipAtLaunch = await remote.SeedAsync("v1");
        var repoId = await SeedCloneableRepositoryAsync(teamId, remote.Url);

        var result = await LaunchAsync(FreshRequest(teamId, userId, repoId));

        await remote.CommitAsync("v2");   // the tip moves on AFTER launch

        (await ReadAgentPinAsync(result.RunId)).ShouldBe(tipAtLaunch,
            "the pin is the tip AT LAUNCH, frozen in the definition snapshot — a remote that advances mid-run can no longer skew participants onto different trees");
    }

    [Fact]
    public async Task An_operator_pinned_BaseBranch_pins_at_that_branchs_tip()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        using var _pauseExec = PauseAutoExecute();

        using var remote = new GitRemote();
        await remote.SeedAsync("main-content");
        var releaseTip = await remote.BranchAsync("release/2.x", "release-content");
        var repoId = await SeedCloneableRepositoryAsync(teamId, remote.Url);

        var result = await LaunchAsync(FreshRequest(teamId, userId, repoId) with { BaseBranch = "release/2.x" });

        (await ReadAgentPinAsync(result.RunId)).ShouldBe(releaseTip, "the vector resolves the OPERATOR'S ref, not the default branch");
    }

    [Fact]
    public async Task A_missing_operator_BaseBranch_fails_the_launch_loud()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        using var _pauseExec = PauseAutoExecute();

        using var remote = new GitRemote();
        await remote.SeedAsync("v1");
        var repoId = await SeedCloneableRepositoryAsync(teamId, remote.Url);

        var ex = await Should.ThrowAsync<WorkspaceException>(() => LaunchAsync(FreshRequest(teamId, userId, repoId) with { BaseBranch = "release/9.x" }));

        ex.Message.ShouldContain("release/9.x", customMessage: "an operator pin that doesn't exist fails at LAUNCH — the clone would fail identically later, never a silent unpinned launch");
    }

    [Fact]
    public async Task An_empty_remote_launches_unpinned_instead_of_failing()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        using var _pauseExec = PauseAutoExecute();

        using var remote = new GitRemote();
        await remote.InitBareAsync();   // a just-created repo: the recorded default branch has no commits yet
        var repoId = await SeedCloneableRepositoryAsync(teamId, remote.Url);

        var result = await LaunchAsync(FreshRequest(teamId, userId, repoId));

        (await ReadAgentPinAsync(result.RunId)).ShouldBeNull("an IMPLICIT default the remote doesn't have launches UNPINNED (the pre-S1 behaviour) — an opportunistic pin must never fail a brand-new repo's launch");
    }

    [Fact]
    public async Task A_deep_launch_bakes_the_pin_into_the_supervisor_profile_and_fails_loud_on_a_bogus_BaseBranch()
    {
        // S1 PR③: the supervisor lane consumes the vector — every spawned agent materializes the launch base, so
        // the deep launch resolves it too (and an operator BaseBranch that doesn't exist now fails THIS launch loud,
        // exactly like the single-agent lane).
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        using var _pauseExec = PauseAutoExecute();

        using var remote = new GitRemote();
        var tip = await remote.SeedAsync("v1");
        var repoId = await SeedCloneableRepositoryAsync(teamId, remote.Url);

        var result = await LaunchAsync(FreshRequest(teamId, userId, repoId) with { RequestedEffort = TaskEffortModes.Deep });

        (await ReadSupervisorProfilePinAsync(result.RunId)).ShouldBe(tip, "the vector bakes into the supervisor node's agentProfile — every spawn reads the same frozen pin");

        var ex = await Should.ThrowAsync<WorkspaceException>(() => LaunchAsync(FreshRequest(teamId, userId, repoId) with { RequestedEffort = TaskEffortModes.Deep, BaseBranch = "release/9.x" }));
        ex.Message.ShouldContain("release/9.x");
    }

    /// <summary>Reads the projected supervisor node's <c>agentProfile.pinnedSha</c> out of the frozen definition snapshot (null when absent ⇒ unpinned).</summary>
    private async Task<string?> ReadSupervisorProfilePinAsync(Guid runId)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var run = await db.WorkflowRun.AsNoTracking().SingleAsync(r => r.Id == runId);

        var sup = JsonDocument.Parse(run.DefinitionSnapshotJson!).RootElement.Clone()
            .GetProperty("nodes").EnumerateArray().Single(n => n.GetProperty("typeKey").GetString() == "agent.supervisor");

        return sup.GetProperty("config").TryGetProperty("agentProfile", out var profile) && profile.TryGetProperty("pinnedSha", out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    }

    [Fact]
    public async Task A_url_less_repo_stays_unpinned_byte_identical()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        using var _pauseExec = PauseAutoExecute();
        var repoId = await SeedUrlLessRepositoryAsync(teamId);

        var result = await LaunchAsync(FreshRequest(teamId, userId, repoId));

        (await ReadAgentPinAsync(result.RunId)).ShouldBeNull("no clone URL ⇒ nothing will ever clone it ⇒ no pin key (byte-identical to before S1)");
    }

    [Fact]
    public async Task A_continuing_repo_on_a_session_branch_stays_unpinned()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        using var _pauseExec = PauseAutoExecute();

        using var remote = new GitRemote();
        await remote.SeedAsync("v1");
        var repoId = await SeedCloneableRepositoryAsync(teamId, remote.Url);
        var sessionId = await SeedSessionWithCodeTurnAsync(teamId, repoId, "run-1/x");

        var result = await LaunchAsync(FreshRequest(teamId, userId, repoId) with { ContinueSessionId = sessionId, TaskText = "Keep going" });

        (await ReadAgentPinAsync(result.RunId)).ShouldBeNull(
            "a session-soft ref's contract is 'the prior branch, or the default if pruned' — a disjunction one commit cannot express, so the continuing repo stays unpinned by design");
    }

    // ─── Helpers ────────────────────────────────────────────────────────────────

    private static TaskLaunchRequest FreshRequest(Guid teamId, Guid userId, Guid repoId) => new()
    {
        TeamId = teamId, ActorUserId = userId, SurfaceKind = TaskLaunchSurfaceKinds.Chat,
        TaskText = "Fix the login bug", RepositoryId = repoId, RequestedEffort = TaskEffortModes.Quick, Autonomy = "Confined",
    };

    private async Task<LaunchTaskResult> LaunchAsync(TaskLaunchRequest request)
    {
        using var scope = _fixture.BeginScope();
        return await scope.Resolve<ITaskLaunchService>().LaunchAsync(request, CancellationToken.None);
    }

    /// <summary>Reads the projected agent.code node's <c>pinnedSha</c> input out of the frozen definition snapshot (null when absent ⇒ unpinned).</summary>
    private async Task<string?> ReadAgentPinAsync(Guid runId)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var run = await db.WorkflowRun.AsNoTracking().SingleAsync(r => r.Id == runId);

        var agent = JsonDocument.Parse(run.DefinitionSnapshotJson!).RootElement.Clone()
            .GetProperty("nodes").EnumerateArray().Single(n => n.GetProperty("id").GetString() == "agent");

        return agent.GetProperty("inputs").TryGetProperty("pinnedSha", out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    }

    private async Task<Guid> SeedCloneableRepositoryAsync(Guid teamId, string cloneUrl)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        var suffix = Guid.NewGuid().ToString("N")[..8];
        var instanceId = Guid.NewGuid();
        var repoId = Guid.NewGuid();

        db.ProviderInstance.Add(new ProviderInstance { Id = instanceId, TeamId = teamId, Provider = ProviderKind.GitHub, DisplayName = "GH", BaseUrl = $"https://gh-{suffix}.local", CreatedBy = SystemUsers.SeederId, LastModifiedBy = SystemUsers.SeederId });
        db.Repository.Add(new Repository { Id = repoId, TeamId = teamId, ProviderInstanceId = instanceId, ExternalId = $"ext-{suffix}", NamespacePath = "acme", Name = "api", FullPath = $"acme/api-{suffix}", WebUrl = "https://gh.local/acme/api", DefaultBranch = "main", CloneUrlHttps = cloneUrl, CreatedBy = SystemUsers.SeederId, LastModifiedBy = SystemUsers.SeederId });

        await db.SaveChangesAsync();
        return repoId;
    }

    private async Task<Guid> SeedUrlLessRepositoryAsync(Guid teamId)
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

    /// <summary>A finished single-repo turn that produced a branch — the minimal shape the session branch resolver reads (mirrors <c>WorkSessionBranchFlowTests.SeedTurnAsync</c>).</summary>
    private async Task<Guid> SeedSessionWithCodeTurnAsync(Guid teamId, Guid repoId, string branch)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        var sessionId = Guid.CreateVersion7();
        db.WorkSession.Add(new WorkSession { Id = sessionId, TeamId = teamId, Title = "thread", Kind = WorkSessionKind.Task, Status = WorkSessionStatus.Open });

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
            Status = WorkflowRunStatus.Success, SessionId = sessionId, SessionTurnIndex = 1,
            ScopeRepositoryIds = new List<Guid> { repoId },
            OutputsJson = JsonSerializer.Serialize(new { branch }),
            CreatedBy = SystemUsers.SeederId, LastModifiedBy = SystemUsers.SeederId,
        });

        await db.SaveChangesAsync();
        return sessionId;
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
        private readonly LaunchBasePinFlowTests _owner;
        public Restore(LaunchBasePinFlowTests owner) { _owner = owner; }
        public void Dispose() => _owner.SetAutoExecute(clearFirst: false, value: true);
    }

    /// <summary>A REAL local git repo standing in for the remote (<c>file://</c> — genuine transport, zero network). GUID-suffixed; best-effort cleanup.</summary>
    private sealed class GitRemote : IDisposable
    {
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "cs-launch-pin-" + Guid.NewGuid().ToString("N"));

        public GitRemote() => Directory.CreateDirectory(_dir);

        public string Url => new Uri(_dir).AbsoluteUri;

        public async Task<string> SeedAsync(string content)
        {
            await RunGitAsync("init", "-b", "main");
            await RunGitAsync("config", "user.email", "test@codespace.dev");
            await RunGitAsync("config", "user.name", "Test");
            await RunGitAsync("config", "commit.gpgsign", "false");
            return await CommitAsync(content);
        }

        public Task InitBareAsync() => RunGitAsync("init", "--bare", "-b", "main");

        public async Task<string> CommitAsync(string content)
        {
            await File.WriteAllTextAsync(Path.Combine(_dir, "file.txt"), content);
            await RunGitAsync("add", ".");
            await RunGitAsync("commit", "-m", "seed");
            return await StdoutAsync("rev-parse", "HEAD");
        }

        /// <summary>Create + commit on a new branch, then return to main. Returns the branch's tip sha.</summary>
        public async Task<string> BranchAsync(string name, string content)
        {
            await RunGitAsync("checkout", "-b", name);
            var sha = await CommitAsync(content);
            await RunGitAsync("checkout", "main");
            return sha;
        }

        private async Task RunGitAsync(params string[] args)
        {
            var result = await new LocalProcessRunner().RunAsync(
                new SandboxSpec { Command = "git", Args = args, WorkingDirectory = _dir, TimeoutSeconds = 60 }, CancellationToken.None);

            if (result.Status != SandboxStatus.Success)
                throw new InvalidOperationException($"git {string.Join(' ', args)} failed: {result.Stderr}");
        }

        private async Task<string> StdoutAsync(params string[] args)
        {
            var result = await new LocalProcessRunner().RunAsync(
                new SandboxSpec { Command = "git", Args = args, WorkingDirectory = _dir, TimeoutSeconds = 60 }, CancellationToken.None);

            if (result.Status != SandboxStatus.Success)
                throw new InvalidOperationException($"git {string.Join(' ', args)} failed: {result.Stderr}");

            return result.Stdout.Trim();
        }

        public void Dispose()
        {
            try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort */ }
        }
    }
}
