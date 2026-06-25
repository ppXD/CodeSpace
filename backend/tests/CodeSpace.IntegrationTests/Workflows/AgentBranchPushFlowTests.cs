using System.Text.Json;
using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Agents.Sandbox;
using CodeSpace.Core.Services.Agents.Sandbox.Runners;
using CodeSpace.Core.Services.Agents.Workspace;
using CodeSpace.Core.Services.Credentials;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Credentials;
using CodeSpace.Messages.Enums;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace CodeSpace.IntegrationTests.Workflows;

/// <summary>
/// HIGH fidelity (Rule 12): drives the REAL <see cref="IAgentRunExecutor"/> (resolved from the fixture scope) all
/// the way through — real <see cref="LocalProcessRunner"/> spawning real <c>git</c> + a real <c>/bin/sh</c> harness,
/// real Postgres, and a real local bare-repo "remote" — to prove the PR-A per-run branch-push opt-in
/// (<see cref="AgentTask.PushProducedBranch"/>) publishes each agent's branch with the deployment-wide env flag
/// <see cref="AgentRunExecutor.PushEnabledEnvVar"/> OFF. Nothing is mocked: each test inspects the ACTUAL refs on the
/// bare remote via <c>git --git-dir</c>, the same way <c>AgentWorkspacePushFlowTests</c> does.
///
/// <para>Covers: (1) per-run opt-in pushes <c>codespace/agent/{runId:N}</c> end-to-end with the env flag off;
/// (2) the load-bearing contrast — env off + NO opt-in → the run still Succeeds but no branch appears (byte-identical
/// to today, proving the opt-in is the trigger, not the mere presence of a token/repo); (3) the fan-out shape — two
/// runs against the SAME bound repo, each opting in, each producing its OWN run-unique branch (N agents = N distinct
/// branches, no collision). The env var is unset around each run and restored in a finally; every OS resource is
/// GUID-suffixed under an IDisposable that best-effort cleans the bare remote + every clone even on the failure path;
/// skips on Windows / when git is absent so a cross-host <c>dotnet test</c> stays clean.</para>
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public sealed class AgentBranchPushFlowTests
{
    private readonly PostgresFixture _fixture;

    public AgentBranchPushFlowTests(PostgresFixture fixture) { _fixture = fixture; }

    [Fact]
    public async Task Per_run_opt_in_pushes_the_branch_end_to_end_with_the_env_flag_off()
    {
        if (OperatingSystem.IsWindows()) return;
        if (!await GitAvailableAsync()) return;

        var original = Environment.GetEnvironmentVariable(AgentRunExecutor.PushEnabledEnvVar);
        try
        {
            // The deployment-wide flag is OFF — so the ONLY thing that can trigger a push is the task's per-run opt-in.
            Environment.SetEnvironmentVariable(AgentRunExecutor.PushEnabledEnvVar, null);

            var teamId = await SeedTeamAsync();
            using var remote = new BareRemote();
            await remote.SeedWithOneCommitAsync();

            var repoId = await SeedBoundRepositoryAsync(teamId, remote.Url, defaultBranch: "main");
            var runId = await CreateRepoRunAsync(teamId, repoId, pushProducedBranch: true);

            // The scripted harness makes a REAL edit in the clone; the executor captures the diff, then — because the
            // per-run opt-in is set — pushes the produced branch to the bare remote over the authenticated clone.
            await ExecuteAsync(runId, new ScriptedHarness("printf 'by the agent\\n' > agent-change.txt; echo edited"));

            using var scope = _fixture.BeginScope();
            var run = await scope.Resolve<IAgentRunService>().GetAsync(runId, CancellationToken.None);
            run.Status.ShouldBe(AgentRunStatus.Succeeded);

            var expectedBranch = AgentRunExecutor.BuildBranchName(runId);
            var result = JsonSerializer.Deserialize<AgentRunResult>(run.ResultJson!, AgentJson.Options)!;
            result.ProducedBranch.ShouldBe(expectedBranch, "the per-run opt-in pushed the diff and folded the produced branch into the result");

            (await remote.HasBranchAsync(expectedBranch)).ShouldBeTrue("the bare remote actually carries the pushed branch");
            (await remote.BranchContainsFileAsync(expectedBranch, "agent-change.txt")).ShouldBeTrue("the pushed branch tip contains the agent's file");
        }
        finally
        {
            Environment.SetEnvironmentVariable(AgentRunExecutor.PushEnabledEnvVar, original);
        }
    }

    [Fact]
    public async Task Env_off_and_no_opt_in_produces_no_branch_byte_identical_to_today()
    {
        if (OperatingSystem.IsWindows()) return;
        if (!await GitAvailableAsync()) return;

        var original = Environment.GetEnvironmentVariable(AgentRunExecutor.PushEnabledEnvVar);
        try
        {
            // Same setup as test 1 — same token, same bound repo, same edit — but the task leaves PushProducedBranch
            // ABSENT. With the env flag also off, the gate is off, so NO branch must appear. This is the load-bearing
            // contrast: it proves the opt-in is what triggers the push, not the presence of a token/repo.
            Environment.SetEnvironmentVariable(AgentRunExecutor.PushEnabledEnvVar, null);

            var teamId = await SeedTeamAsync();
            using var remote = new BareRemote();
            await remote.SeedWithOneCommitAsync();

            var repoId = await SeedBoundRepositoryAsync(teamId, remote.Url, defaultBranch: "main");
            var runId = await CreateRepoRunAsync(teamId, repoId, pushProducedBranch: null);

            await ExecuteAsync(runId, new ScriptedHarness("printf 'by the agent\\n' > agent-change.txt; echo edited"));

            using var scope = _fixture.BeginScope();
            var run = await scope.Resolve<IAgentRunService>().GetAsync(runId, CancellationToken.None);
            run.Status.ShouldBe(AgentRunStatus.Succeeded, "the run still runs to success — only the side-effecting push is gated off");

            var result = JsonSerializer.Deserialize<AgentRunResult>(run.ResultJson!, AgentJson.Options)!;
            result.ProducedBranch.ShouldBeNull("no opt-in + env off → the gate is closed → no branch is produced");

            (await remote.HasBranchAsync(AgentRunExecutor.BuildBranchName(runId))).ShouldBeFalse("the bare remote gained NO branch");
        }
        finally
        {
            Environment.SetEnvironmentVariable(AgentRunExecutor.PushEnabledEnvVar, original);
        }
    }

    [Fact]
    public async Task Fan_out_two_opted_in_runs_each_push_their_own_distinct_branch()
    {
        if (OperatingSystem.IsWindows()) return;
        if (!await GitAvailableAsync()) return;

        var original = Environment.GetEnvironmentVariable(AgentRunExecutor.PushEnabledEnvVar);
        try
        {
            // The one-agent-one-branch fan-out shape, proven at the executor level (simpler than the full flow.map
            // engine, and enough to prove the distinct-branch property): two agent runs against the SAME bound repo,
            // each opting in, each writing its OWN file. The branch name is run-id-derived, so N agents = N distinct
            // branches with no collision — both must land on the remote with their respective files.
            Environment.SetEnvironmentVariable(AgentRunExecutor.PushEnabledEnvVar, null);

            var teamId = await SeedTeamAsync();
            using var remote = new BareRemote();
            await remote.SeedWithOneCommitAsync();

            var repoId = await SeedBoundRepositoryAsync(teamId, remote.Url, defaultBranch: "main");

            var runIdA = await CreateRepoRunAsync(teamId, repoId, pushProducedBranch: true);
            var runIdB = await CreateRepoRunAsync(teamId, repoId, pushProducedBranch: true);

            await ExecuteAsync(runIdA, new ScriptedHarness("printf 'agent A\\n' > agent-a.txt; echo edited"));
            await ExecuteAsync(runIdB, new ScriptedHarness("printf 'agent B\\n' > agent-b.txt; echo edited"));

            var branchA = AgentRunExecutor.BuildBranchName(runIdA);
            var branchB = AgentRunExecutor.BuildBranchName(runIdB);

            branchA.ShouldNotBe(branchB, "each run derives its own run-unique branch — no collision");

            using var scope = _fixture.BeginScope();
            var svc = scope.Resolve<IAgentRunService>();
            (await svc.GetAsync(runIdA, CancellationToken.None)).Status.ShouldBe(AgentRunStatus.Succeeded);
            (await svc.GetAsync(runIdB, CancellationToken.None)).Status.ShouldBe(AgentRunStatus.Succeeded);

            (await remote.HasBranchAsync(branchA)).ShouldBeTrue("agent A's branch is on the remote");
            (await remote.HasBranchAsync(branchB)).ShouldBeTrue("agent B's branch is on the remote");
            (await remote.BranchContainsFileAsync(branchA, "agent-a.txt")).ShouldBeTrue("agent A's branch carries agent A's file");
            (await remote.BranchContainsFileAsync(branchB, "agent-b.txt")).ShouldBeTrue("agent B's branch carries agent B's file");
        }
        finally
        {
            Environment.SetEnvironmentVariable(AgentRunExecutor.PushEnabledEnvVar, original);
        }
    }

    // ─── Seeding ─────────────────────────────────────────────────────────────

    private async Task<Guid> CreateRepoRunAsync(Guid teamId, Guid repositoryId, bool? pushProducedBranch)
    {
        using var scope = _fixture.BeginScope();
        var run = await scope.Resolve<IAgentRunService>().CreateAsync(
            new AgentTask { Goal = "edit", Harness = "scripted", Model = "test-model", RepositoryId = repositoryId, PushProducedBranch = pushProducedBranch },
            teamId, null, null, iterationKey: "", cancellationToken: CancellationToken.None);
        return run.Id;
    }

    /// <summary>
    /// Seed a Repository bound to a GitHub PAT Credential whose decrypted token the auth resolver returns — so the
    /// executor's clone CARRIES a token and <c>LocalGitWorkspaceProvider</c> takes the authenticated push path
    /// (a token-less clone short-circuits the push to null by design). Provider = GitHub so TokenUsernameFor is
    /// "x-access-token"; the Credential, ProviderInstance, and Repository all share the team.
    /// </summary>
    private async Task<Guid> SeedBoundRepositoryAsync(Guid teamId, string cloneUrlHttps, string defaultBranch)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        var instanceId = Guid.NewGuid();
        db.ProviderInstance.Add(new ProviderInstance { Id = instanceId, TeamId = teamId, Provider = ProviderKind.GitHub, DisplayName = "local", BaseUrl = "https://local" });

        var serializer = scope.Resolve<ICredentialPayloadSerializer>();
        var encryptor = scope.Resolve<IPayloadEncryptor>();
        var payloadJson = serializer.Serialize(new PatPayload { Token = "agent-clone-token" });

        var credentialId = Guid.NewGuid();
        db.Credential.Add(new Credential
        {
            Id = credentialId, TeamId = teamId, ProviderInstanceId = instanceId,
            AuthType = AuthType.Pat, DisplayName = "clone cred",
            EncryptedPayload = encryptor.Encrypt(payloadJson), Status = CredentialStatus.Active,
        });

        var repoId = Guid.NewGuid();
        db.Repository.Add(new Repository
        {
            Id = repoId, TeamId = teamId, ProviderInstanceId = instanceId, CredentialId = credentialId,
            ExternalId = repoId.ToString(), NamespacePath = "org", Name = "repo", FullPath = "org/repo",
            DefaultBranch = defaultBranch, CloneUrlHttps = cloneUrlHttps, WebUrl = "https://local/org/repo",
        });

        await db.SaveChangesAsync();
        return repoId;
    }

    private async Task<Guid> SeedTeamAsync()
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        var userId = Guid.NewGuid();
        db.User.Add(new User { Id = userId, Email = $"agent-{userId:N}@test.local", Name = $"agent-{userId:N}" });

        var teamId = Guid.NewGuid();
        db.Team.Add(new Team { Id = teamId, Slug = $"agent-{teamId:N}", Name = "Agent Team", Kind = TeamKind.Workspace, OwnerUserId = userId });
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
            new HarnessModelReconciler(new AgentHarnessRegistry(new[] { harness }), scope.Resolve<CodeSpaceDbContext>()),
            scope.Resolve<ISandboxRunnerRegistry>(),
            scope.Resolve<IAgentWorkspaceResolver>(),
            scope.Resolve<IModelCredentialResolver>(),
            scope.Resolve<IWorkspaceProviderRegistry>(),
            scope.Resolve<IAgentRunCompletionNotifier>(),
            scope.Resolve<Microsoft.Extensions.DependencyInjection.IServiceScopeFactory>(),
            scope.Resolve<CodeSpaceDbContext>(),
            NullLogger<AgentRunExecutor>.Instance);

        await executor.ExecuteAsync(runId, CancellationToken.None);
    }

    // ─── Git helpers ───────────────────────────────────────────────────────────

    private static async Task<bool> GitAvailableAsync()
    {
        try { return (await new LocalProcessRunner().RunAsync(new SandboxSpec { Command = "git", Args = new[] { "--version" }, TimeoutSeconds = 10 }, CancellationToken.None)).Status == SandboxStatus.Success; }
        catch { return false; }
    }

    /// <summary>A bare local repo standing in for the agent's remote, plus ref inspection via <c>git --git-dir</c> — the real-git ground truth the push lands on. GUID-suffixed; IDisposable best-effort cleans every dir even on the failure path.</summary>
    private sealed class BareRemote : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "cs-agent-push-" + Guid.NewGuid().ToString("N"));
        private readonly string _bare;

        public BareRemote()
        {
            Directory.CreateDirectory(_root);
            _bare = Path.Combine(_root, "remote.git");
        }

        /// <summary>The file:// URL the bound Repository's CloneUrlHttps points at — git ignores file:// userinfo, so a token-carrying clone still pushes here.</summary>
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

        public async Task<bool> BranchContainsFileAsync(string branch, string file) =>
            (await RunGitAsync(_root, "--git-dir", _bare, "ls-tree", "-r", "--name-only", branch)).Split('\n').Any(l => l.Trim() == file);

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

    /// <summary>A CLI-less test harness: builds a /bin/sh invocation from a fixed script, wraps each stdout line as an assistant message, and folds the exit code. Mirrors AgentRunExecutorTests' ScriptedHarness so the push path runs through the real executor exactly as a production harness would.</summary>
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
