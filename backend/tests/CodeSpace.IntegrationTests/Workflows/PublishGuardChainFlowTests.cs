using System.Text.Json;
using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Agents.ModelCredentials;
using CodeSpace.Core.Services.Agents.Publish;
using CodeSpace.Core.Services.Agents.Sandbox;
using CodeSpace.Core.Services.Agents.Sandbox.Runners;
using CodeSpace.Core.Services.Agents.Workspace;
using CodeSpace.Core.Services.Credentials;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Credentials;
using CodeSpace.Messages.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace CodeSpace.IntegrationTests.Workflows;

/// <summary>
/// HIGH fidelity (Rule 12): the two policy guards <see cref="AgentBranchPushFlowTests"/> doesn't cover — driven
/// through the REAL <see cref="IAgentRunExecutor"/> against real Postgres, real git, and a real local bare-repo
/// remote. Both prove the SAME shape: the diff still gets captured + recorded in the <see cref="PublishManifest"/>
/// (I1 holds), only the PUSH is skipped, and the winning guard's reason is readable back off the manifest row's
/// <c>Summary</c> — never a silent drop.
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public sealed class PublishGuardChainFlowTests
{
    private readonly PostgresFixture _fixture;

    public PublishGuardChainFlowTests(PostgresFixture fixture) { _fixture = fixture; }

    [Fact]
    public async Task A_repository_with_no_bound_credential_captures_the_diff_but_never_pushes()
    {
        if (OperatingSystem.IsWindows()) return;
        if (!await GitAvailableAsync()) return;

        var teamId = await SeedTeamAsync();
        using var remote = new BareRemote();
        await remote.SeedWithOneCommitAsync();

        var repoId = await SeedRepositoryAsync(teamId, remote.Url, defaultBranch: "main", credentialId: null, publishMode: RepositoryPublishMode.Branch);
        var runId = await CreateRepoRunAsync(teamId, repoId);

        await ExecuteAsync(runId, new ScriptedHarness("printf 'by the agent\\n' > agent-change.txt; echo edited"));

        using var scope = _fixture.BeginScope();
        var run = await scope.Resolve<IAgentRunService>().GetAsync(runId, CancellationToken.None);
        run.Status.ShouldBe(AgentRunStatus.Succeeded, "a missing credential is a publish POLICY decision, never a run failure");

        var result = JsonSerializer.Deserialize<AgentRunResult>(run.ResultJson!, AgentJson.Options)!;
        result.ChangedFiles.ShouldContain("agent-change.txt", "I1 holds regardless of the guard — the diff is captured either way");
        result.ProducedBranch.ShouldBeNull();
        result.PublishSkipReason.ShouldBe("the repository has no bound push credential");

        (await remote.HasBranchAsync(AgentRunExecutor.BuildBranchName(runId))).ShouldBeFalse("no credential → no push attempt at all, so no branch on the remote");

        var manifest = (await scope.Resolve<IPublishManifestStore>().ListForAgentRunAsync(runId, teamId, CancellationToken.None)).ShouldHaveSingleItem();
        manifest.PublishStateValue.ShouldBe(PublishState.PatchOnly);
        manifest.ChangedFileCount.ShouldBe(1, "the manifest records the captured diff even though nothing was pushed");
        manifest.Summary.ShouldBe("the repository has no bound push credential", "the guard's reason is readable straight off the manifest row — never a silent skip");
    }

    [Fact]
    public async Task A_repository_with_publish_mode_patch_only_captures_the_diff_but_never_pushes()
    {
        if (OperatingSystem.IsWindows()) return;
        if (!await GitAvailableAsync()) return;

        var teamId = await SeedTeamAsync();
        using var remote = new BareRemote();
        await remote.SeedWithOneCommitAsync();

        // A bound credential (so NoCredentialPublishGuard clears) with the repo-level policy override set — isolates
        // RepositoryPolicyPublishGuard as the ONE guard that fires.
        var repoId = await SeedRepositoryAsync(teamId, remote.Url, defaultBranch: "main", credentialId: await SeedCredentialAsync(teamId), publishMode: RepositoryPublishMode.PatchOnly);
        var runId = await CreateRepoRunAsync(teamId, repoId);

        await ExecuteAsync(runId, new ScriptedHarness("printf 'by the agent\\n' > agent-change.txt; echo edited"));

        using var scope = _fixture.BeginScope();
        var run = await scope.Resolve<IAgentRunService>().GetAsync(runId, CancellationToken.None);
        run.Status.ShouldBe(AgentRunStatus.Succeeded);

        var result = JsonSerializer.Deserialize<AgentRunResult>(run.ResultJson!, AgentJson.Options)!;
        result.ChangedFiles.ShouldContain("agent-change.txt", "I1 holds regardless of the guard — the diff is captured either way");
        result.ProducedBranch.ShouldBeNull();
        result.PublishSkipReason.ShouldBe("the repository requires patch-only publishing");

        (await remote.HasBranchAsync(AgentRunExecutor.BuildBranchName(runId))).ShouldBeFalse("the repo-level policy blocks the push even though a valid credential exists");

        var manifest = (await scope.Resolve<IPublishManifestStore>().ListForAgentRunAsync(runId, teamId, CancellationToken.None)).ShouldHaveSingleItem();
        manifest.PublishStateValue.ShouldBe(PublishState.PatchOnly);
        manifest.Summary.ShouldBe("the repository requires patch-only publishing");
    }

    [Fact]
    public async Task Changing_publish_mode_back_to_branch_lets_a_later_run_push_normally()
    {
        // Proves the policy is read FRESH per run (not cached/sticky) — a repo flipped back to Branch afterwards
        // publishes normally, exactly like AgentBranchPushFlowTests' default-push case.
        if (OperatingSystem.IsWindows()) return;
        if (!await GitAvailableAsync()) return;

        var teamId = await SeedTeamAsync();
        using var remote = new BareRemote();
        await remote.SeedWithOneCommitAsync();

        var credentialId = await SeedCredentialAsync(teamId);
        var repoId = await SeedRepositoryAsync(teamId, remote.Url, defaultBranch: "main", credentialId: credentialId, publishMode: RepositoryPublishMode.PatchOnly);

        var blockedRunId = await CreateRepoRunAsync(teamId, repoId);
        await ExecuteAsync(blockedRunId, new ScriptedHarness("printf 'blocked\\n' > blocked.txt; echo edited"));

        using (var scope = _fixture.BeginScope())
        {
            var db = scope.Resolve<CodeSpaceDbContext>();
            var repo = await db.Repository.SingleAsync(r => r.Id == repoId);
            repo.PublishMode = RepositoryPublishMode.Branch;
            await db.SaveChangesAsync();
        }

        var allowedRunId = await CreateRepoRunAsync(teamId, repoId);
        await ExecuteAsync(allowedRunId, new ScriptedHarness("printf 'allowed\\n' > allowed.txt; echo edited"));

        using var read = _fixture.BeginScope();
        var blockedResult = JsonSerializer.Deserialize<AgentRunResult>((await read.Resolve<IAgentRunService>().GetAsync(blockedRunId, CancellationToken.None)).ResultJson!, AgentJson.Options)!;
        var allowedResult = JsonSerializer.Deserialize<AgentRunResult>((await read.Resolve<IAgentRunService>().GetAsync(allowedRunId, CancellationToken.None)).ResultJson!, AgentJson.Options)!;

        blockedResult.ProducedBranch.ShouldBeNull();
        allowedResult.ProducedBranch.ShouldBe(AgentRunExecutor.BuildBranchName(allowedRunId), "the policy flip took effect for the NEXT run — the guard reads Repository fresh, not a cached snapshot");

        (await remote.HasBranchAsync(AgentRunExecutor.BuildBranchName(allowedRunId))).ShouldBeTrue();
    }

    // ─── Seeding ─────────────────────────────────────────────────────────────

    private async Task<Guid> CreateRepoRunAsync(Guid teamId, Guid repositoryId)
    {
        using var scope = _fixture.BeginScope();
        var run = await scope.Resolve<IAgentRunService>().CreateAsync(
            new AgentTask { Goal = "edit", Harness = "scripted", Model = "test-model", RepositoryId = repositoryId },
            teamId, null, null, iterationKey: "", cancellationToken: CancellationToken.None);
        return run.Id;
    }

    private async Task<Guid> SeedCredentialAsync(Guid teamId)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        var instanceId = await FindOrCreateProviderInstanceAsync(db, teamId);

        var serializer = scope.Resolve<ICredentialPayloadSerializer>();
        var encryptor = scope.Resolve<IPayloadEncryptor>();
        var payloadJson = serializer.Serialize(new PatPayload { Token = "guard-chain-e2e-token" });

        var credentialId = Guid.NewGuid();
        db.Credential.Add(new Credential
        {
            Id = credentialId, TeamId = teamId, ProviderInstanceId = instanceId,
            AuthType = AuthType.Pat, DisplayName = "clone cred",
            EncryptedPayload = encryptor.Encrypt(payloadJson), Status = CredentialStatus.Active,
        });

        await db.SaveChangesAsync();
        return credentialId;
    }

    private async Task<Guid> FindOrCreateProviderInstanceAsync(CodeSpaceDbContext db, Guid teamId)
    {
        var existing = await db.ProviderInstance.Where(p => p.TeamId == teamId).Select(p => p.Id).FirstOrDefaultAsync();
        if (existing != Guid.Empty) return existing;

        var instanceId = Guid.NewGuid();
        db.ProviderInstance.Add(new ProviderInstance { Id = instanceId, TeamId = teamId, Provider = ProviderKind.GitHub, DisplayName = "local", BaseUrl = "https://local" });
        await db.SaveChangesAsync();
        return instanceId;
    }

    private async Task<Guid> SeedRepositoryAsync(Guid teamId, string cloneUrl, string defaultBranch, Guid? credentialId, RepositoryPublishMode publishMode)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        var instanceId = await FindOrCreateProviderInstanceAsync(db, teamId);

        var repoId = Guid.NewGuid();
        db.Repository.Add(new Repository
        {
            Id = repoId, TeamId = teamId, ProviderInstanceId = instanceId, CredentialId = credentialId,
            ExternalId = repoId.ToString(), NamespacePath = "org", Name = "repo", FullPath = "org/repo",
            DefaultBranch = defaultBranch, CloneUrlHttps = cloneUrl, WebUrl = "https://local/org/repo",
            PublishMode = publishMode,
        });

        await db.SaveChangesAsync();
        return repoId;
    }

    private async Task<Guid> SeedTeamAsync()
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        var userId = Guid.NewGuid();
        db.User.Add(new User { Id = userId, Email = $"guard-{userId:N}@test.local", Name = $"guard-{userId:N}" });

        var teamId = Guid.NewGuid();
        db.Team.Add(new Team { Id = teamId, Slug = $"guard-{teamId:N}", Name = "Guard Team", Kind = TeamKind.Workspace, OwnerUserId = userId });
        db.TeamMembership.Add(new TeamMembership { Id = Guid.NewGuid(), TeamId = teamId, UserId = userId, Role = TeamRole.Owner });

        await db.SaveChangesAsync();
        return teamId;
    }

    // ─── Execution ───────────────────────────────────────────────────────────

    private async Task ExecuteAsync(Guid runId, IAgentHarness harness)
    {
        using var scope = _fixture.BeginScope();
        var executor = new AgentRunExecutor(
            scope.Resolve<IAgentRunService>(),
            new AgentHarnessRegistry(new[] { harness }),
            new HarnessModelReconciler(new AgentHarnessRegistry(new[] { harness }), scope.Resolve<IModelPoolSelector>(), scope.Resolve<CodeSpaceDbContext>()),
            scope.Resolve<ISandboxRunnerRegistry>(),
            scope.Resolve<IAgentWorkspaceResolver>(),
            scope.Resolve<IModelCredentialResolver>(),
            scope.Resolve<IWorkspaceProviderRegistry>(),
            scope.Resolve<IAgentRunCompletionNotifier>(),
            scope.Resolve<Microsoft.Extensions.DependencyInjection.IServiceScopeFactory>(),
            scope.Resolve<CodeSpaceDbContext>(),
            scope.Resolve<CodeSpace.Core.Services.Review.IStructuredCritic>(),
            scope.Resolve<CodeSpace.Core.Services.Workflows.Artifacts.IArtifactOffloader>(),
            scope.Resolve<IPublishManifestStore>(),
            scope.Resolve<IEnumerable<IPublishGuard>>(),
            NullLogger<AgentRunExecutor>.Instance);

        await executor.ExecuteAsync(runId, CancellationToken.None);
    }

    // ─── Git helpers ───────────────────────────────────────────────────────────

    private static async Task<bool> GitAvailableAsync()
    {
        try { return (await new LocalProcessRunner().RunAsync(new SandboxSpec { Command = "git", Args = new[] { "--version" }, TimeoutSeconds = 10 }, CancellationToken.None)).Status == SandboxStatus.Success; }
        catch { return false; }
    }

    /// <summary>A bare local repo standing in for the agent's remote, plus ref inspection via <c>git --git-dir</c> — real-git ground truth. GUID-suffixed; IDisposable best-effort cleans every dir even on the failure path. Mirrors <see cref="AgentBranchPushFlowTests.BareRemote"/>.</summary>
    private sealed class BareRemote : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "cs-guard-chain-" + Guid.NewGuid().ToString("N"));
        private readonly string _bare;

        public BareRemote()
        {
            Directory.CreateDirectory(_root);
            _bare = Path.Combine(_root, "remote.git");
        }

        public string Url => new Uri(_bare).AbsoluteUri;

        public async Task SeedWithOneCommitAsync()
        {
            await RunGitAsync(_root, "init", "--bare", "-b", "main", _bare);

            var seed = Path.Combine(_root, "seed");
            Directory.CreateDirectory(seed);
            await RunGitAsync(seed, "clone", _bare, seed);
            await RunGitAsync(seed, "config", "user.email", "test@codespace.dev");
            await RunGitAsync(seed, "config", "user.name", "Test");
            await RunGitAsync(seed, "config", "commit.gpgsign", "false");
            await File.WriteAllTextAsync(Path.Combine(seed, "README.md"), "base");
            await RunGitAsync(seed, "add", ".");
            await RunGitAsync(seed, "commit", "-m", "seed");
            await RunGitAsync(seed, "push", "origin", "main");
        }

        public async Task<bool> HasBranchAsync(string branch) =>
            (await RunGitAsync(_root, "--git-dir", _bare, "branch", "--list", branch)).Trim().Length > 0;

        private static async Task<string> RunGitAsync(string workdir, params string[] args)
        {
            var result = await new LocalProcessRunner().RunAsync(
                new SandboxSpec { Command = "git", Args = args, WorkingDirectory = workdir, TimeoutSeconds = 60 }, CancellationToken.None);

            if (result.Status != SandboxStatus.Success)
                throw new InvalidOperationException($"git {string.Join(' ', args)} failed: {result.Stderr}");

            return result.Stdout;
        }

        public void Dispose()
        {
            try { Directory.Delete(_root, recursive: true); } catch { /* best-effort */ }
        }
    }

    /// <summary>A CLI-less test harness: builds a /bin/sh invocation from a fixed script, wraps each stdout line as an assistant message, and folds the exit code. Mirrors <see cref="AgentBranchPushFlowTests.ScriptedHarness"/>.</summary>
    private sealed class ScriptedHarness : IAgentHarness
    {
        private readonly string _script;

        public ScriptedHarness(string script) => _script = script;

        public string Kind => "scripted";
        public string Version => "test";
        public IReadOnlyList<string> Models { get; } = new[] { "test-model" };

        public SandboxSpec BuildInvocation(AgentTask task) => new() { Command = "/bin/sh", Args = new[] { "-c", _script }, WorkingDirectory = task.WorkspaceDirectory, TimeoutSeconds = task.TimeoutSeconds };

        public IReadOnlyList<AgentEvent> ParseEvents(string rawLine) =>
            string.IsNullOrWhiteSpace(rawLine) ? Array.Empty<AgentEvent>() : new[] { new AgentEvent { Kind = AgentEventKind.AssistantMessage, Text = rawLine.Trim() } };

        public AgentRunResult BuildResult(IReadOnlyList<AgentEvent> events, int exitCode) =>
            exitCode == 0
                ? new AgentRunResult { Status = AgentRunStatus.Succeeded, ExitReason = "completed", Summary = events.Count > 0 ? events[^1].Text : null }
                : new AgentRunResult { Status = AgentRunStatus.Failed, ExitReason = "non-zero-exit", Error = $"exit {exitCode}" };
    }
}
