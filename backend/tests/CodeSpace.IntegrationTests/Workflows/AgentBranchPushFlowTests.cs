using System.Text.Json;
using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Agents.ModelCredentials;
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
/// real Postgres, and a real local bare-repo "remote" — to prove push is DEFAULT-ON for a non-empty diff (PR-2's
/// env-gate deletion) and that the publish guard chain's explicit opt-out actually blocks it end-to-end. Nothing is
/// mocked: each test inspects the ACTUAL refs on the bare remote via <c>git --git-dir</c>.
///
/// <para>Covers: (1) an ORDINARY task (no <see cref="AgentTask.PushProducedBranch"/> set at all) pushes
/// <c>codespace/agent/{runId:N}</c> end-to-end — no opt-in needed, proving the flip; (2) the load-bearing contrast —
/// an EXPLICIT opt-out (<c>PushProducedBranch = false</c>, the <see cref="Core.Services.Agents.Publish.Guards.ProfileOptOutPublishGuard"/>)
/// still runs the diff to Succeeded but produces NO branch, proving the guard is a real, working brake, not
/// decorative; (3) the fan-out shape — two default-push runs against the SAME bound repo each produce their OWN
/// run-unique branch (N agents = N distinct branches, no collision). Every OS resource is GUID-suffixed under an
/// IDisposable that best-effort cleans the bare remote + every clone even on the failure path; skips on Windows /
/// when git is absent so a cross-host <c>dotnet test</c> stays clean.</para>
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public sealed class AgentBranchPushFlowTests
{
    private readonly PostgresFixture _fixture;

    public AgentBranchPushFlowTests(PostgresFixture fixture) { _fixture = fixture; }

    [Fact]
    public async Task Default_push_publishes_the_branch_end_to_end_with_no_opt_in()
    {
        if (OperatingSystem.IsWindows()) return;
        if (!await GitAvailableAsync()) return;

        var teamId = await SeedTeamAsync();
        using var remote = new BareRemote();
        await remote.SeedWithOneCommitAsync();

        var repoId = await SeedBoundRepositoryAsync(teamId, remote.Url, defaultBranch: "main");
        var runId = await CreateRepoRunAsync(teamId, repoId, pushProducedBranch: null);   // no per-run signal at all

        // The scripted harness makes a REAL edit in the clone; the executor captures the diff, then — because push
        // is DEFAULT-ON for a non-empty diff — pushes the produced branch to the bare remote over the authenticated clone.
        await ExecuteAsync(runId, new ScriptedHarness("printf 'by the agent\\n' > agent-change.txt; echo edited"));

        using var scope = _fixture.BeginScope();
        var run = await scope.Resolve<IAgentRunService>().GetAsync(runId, CancellationToken.None);
        run.Status.ShouldBe(AgentRunStatus.Succeeded);

        var expectedBranch = AgentRunExecutor.BuildBranchName(runId);
        var result = JsonSerializer.Deserialize<AgentRunResult>(run.ResultJson!, AgentJson.Options)!;
        result.ProducedBranch.ShouldBe(expectedBranch, "push is default-on — no opt-in needed to publish the produced branch");

        (await remote.HasBranchAsync(expectedBranch)).ShouldBeTrue("the bare remote actually carries the pushed branch");
        (await remote.BranchContainsFileAsync(expectedBranch, "agent-change.txt")).ShouldBeTrue("the pushed branch tip contains the agent's file");
    }

    [Fact]
    public async Task Explicit_opt_out_produces_no_branch_end_to_end()
    {
        if (OperatingSystem.IsWindows()) return;
        if (!await GitAvailableAsync()) return;

        // Same setup as the default-push test — same token, same bound repo, same edit — but the task explicitly
        // opts OUT. This is the load-bearing contrast: it proves the ProfileOptOutPublishGuard is a REAL, working
        // brake against a real git clone/push, not merely correct in the unit-level fake.
        var teamId = await SeedTeamAsync();
        using var remote = new BareRemote();
        await remote.SeedWithOneCommitAsync();

        var repoId = await SeedBoundRepositoryAsync(teamId, remote.Url, defaultBranch: "main");
        var runId = await CreateRepoRunAsync(teamId, repoId, pushProducedBranch: false);

        await ExecuteAsync(runId, new ScriptedHarness("printf 'by the agent\\n' > agent-change.txt; echo edited"));

        using var scope = _fixture.BeginScope();
        var run = await scope.Resolve<IAgentRunService>().GetAsync(runId, CancellationToken.None);
        run.Status.ShouldBe(AgentRunStatus.Succeeded, "the run still runs to success — only the side-effecting push is gated off");

        var result = JsonSerializer.Deserialize<AgentRunResult>(run.ResultJson!, AgentJson.Options)!;
        result.ProducedBranch.ShouldBeNull("the explicit opt-out guard blocks the push");
        result.PublishSkipReason.ShouldBe("push disabled by the launch profile");

        (await remote.HasBranchAsync(AgentRunExecutor.BuildBranchName(runId))).ShouldBeFalse("the bare remote gained NO branch");
    }

    [Fact]
    public async Task Fan_out_two_default_push_runs_each_push_their_own_distinct_branch()
    {
        if (OperatingSystem.IsWindows()) return;
        if (!await GitAvailableAsync()) return;

        // The one-agent-one-branch fan-out shape, proven at the executor level (simpler than the full flow.map
        // engine, and enough to prove the distinct-branch property): two DEFAULT-push agent runs (no opt-in) against
        // the SAME bound repo, each writing its OWN file. The branch name is run-id-derived, so N agents = N distinct
        // branches with no collision — both must land on the remote with their respective files.
        var teamId = await SeedTeamAsync();
        using var remote = new BareRemote();
        await remote.SeedWithOneCommitAsync();

        var repoId = await SeedBoundRepositoryAsync(teamId, remote.Url, defaultBranch: "main");

        var runIdA = await CreateRepoRunAsync(teamId, repoId, pushProducedBranch: null);
        var runIdB = await CreateRepoRunAsync(teamId, repoId, pushProducedBranch: null);

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

    [Fact]
    public async Task P2_2_a_timed_out_run_salvages_its_real_edit_as_a_real_pushed_branch()
    {
        // P2.2 end-to-end through the REAL completion pipeline (real Postgres, real git clone + authenticated push
        // against a real bare remote — only the codex/claude binary is faked): the scripted harness writes a REAL
        // file to the clone, THEN sleeps past the 1s wall clock. CodeSpace force-kills it, but the write already
        // reached disk before the kill — git ground truth, independent of the kill signal — so it must land as a
        // real branch on the remote instead of vanishing with the killed process.
        if (OperatingSystem.IsWindows()) return;
        if (!await GitAvailableAsync()) return;

        var original = Environment.GetEnvironmentVariable(AgentRunExecutor.PushEnabledEnvVar);
        try
        {
            Environment.SetEnvironmentVariable(AgentRunExecutor.PushEnabledEnvVar, null);

            var teamId = await SeedTeamAsync();
            using var remote = new BareRemote();
            await remote.SeedWithOneCommitAsync();

            var repoId = await SeedBoundRepositoryAsync(teamId, remote.Url, defaultBranch: "main");
            var runId = await CreateRepoRunAsync(teamId, repoId, pushProducedBranch: true, timeoutSeconds: 1);

            await ExecuteAsync(runId, new ScriptedHarness("printf 'salvaged edit\\n' > salvaged.txt; sleep 10"));

            using var scope = _fixture.BeginScope();
            var run = await scope.Resolve<IAgentRunService>().GetAsync(runId, CancellationToken.None);
            run.Status.ShouldBe(AgentRunStatus.TimedOut);

            var expectedBranch = AgentRunExecutor.BuildBranchName(runId);
            var result = JsonSerializer.Deserialize<AgentRunResult>(run.ResultJson!, AgentJson.Options)!;
            result.ChangedFiles.ShouldContain("salvaged.txt", "the file the agent wrote before the kill is captured as real git ground truth");
            result.ProducedBranch.ShouldBe(expectedBranch, "the real edit is salvaged as a real pushed branch instead of vanishing with the killed process");

            (await remote.HasBranchAsync(expectedBranch)).ShouldBeTrue("the bare remote genuinely carries the salvaged branch, not just result_jsonb");
            (await remote.BranchContainsFileAsync(expectedBranch, "salvaged.txt")).ShouldBeTrue("the pushed branch tip contains the killed agent's real edit");
        }
        finally
        {
            Environment.SetEnvironmentVariable(AgentRunExecutor.PushEnabledEnvVar, original);
        }
    }

    // ─── Seeding ─────────────────────────────────────────────────────────────

    private async Task<Guid> CreateRepoRunAsync(Guid teamId, Guid repositoryId, bool? pushProducedBranch, int? timeoutSeconds = null)
    {
        using var scope = _fixture.BeginScope();
        var run = await scope.Resolve<IAgentRunService>().CreateAsync(
            new AgentTask { Goal = "edit", Harness = "scripted", Model = "test-model", RepositoryId = repositoryId, PushProducedBranch = pushProducedBranch, TimeoutSeconds = timeoutSeconds },
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
            scope.Resolve<CodeSpace.Core.Services.Agents.Publish.IPublishManifestStore>(),
            scope.Resolve<IEnumerable<CodeSpace.Core.Services.Agents.Publish.IPublishGuard>>(),
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
