using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Agents.Sandbox;
using CodeSpace.Core.Services.Agents.Sandbox.Runners;
using CodeSpace.Core.Services.Agents.Workspace;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Enums;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace CodeSpace.IntegrationTests.Workflows;

/// <summary>
/// Drives the agent run executor end-to-end against real Postgres + the real LocalProcessRunner, with a
/// scripted test harness standing in for a CLI (so a real /bin/sh process actually runs). Proves the
/// full execution path — claim → stream events → land result — plus the exactly-once guard (an already-
/// claimed run is never re-spawned), failure, and timeout mapping.
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public class AgentRunExecutorTests
{
    private readonly PostgresFixture _fixture;

    public AgentRunExecutorTests(PostgresFixture fixture) { _fixture = fixture; }

    [Fact]
    public async Task Executes_end_to_end_streaming_events_and_completing_succeeded()
    {
        if (OperatingSystem.IsWindows()) return;

        var teamId = await SeedTeamAsync();
        var runId = await CreateScriptedRunAsync(teamId);

        await ExecuteAsync(runId, new ScriptedHarness("printf 'step one\\nstep two\\nstep three\\n'"));

        using var scope = _fixture.BeginScope();
        var svc = scope.Resolve<IAgentRunService>();

        var run = await svc.GetAsync(runId, CancellationToken.None);
        run.Status.ShouldBe(AgentRunStatus.Succeeded);
        run.StartedAt.ShouldNotBeNull();
        run.CompletedAt.ShouldNotBeNull();
        run.ResultJson.ShouldNotBeNull();

        var events = await svc.GetEventsAsync(runId, 0, CancellationToken.None);
        events.Select(e => e.Text).ShouldBe(new[] { "step one", "step two", "step three" });
    }

    [Fact]
    public async Task Does_not_re_run_an_already_claimed_run()
    {
        if (OperatingSystem.IsWindows()) return;

        var teamId = await SeedTeamAsync();
        var runId = await CreateScriptedRunAsync(teamId);

        using (var scope = _fixture.BeginScope())
            await scope.Resolve<IAgentRunService>().MarkRunningAsync(runId, CancellationToken.None);   // another worker already claimed it

        await ExecuteAsync(runId, new ScriptedHarness("printf 'must not run\\n'"));

        using var verify = _fixture.BeginScope();
        var svc = verify.Resolve<IAgentRunService>();
        (await svc.GetAsync(runId, CancellationToken.None)).Status.ShouldBe(AgentRunStatus.Running);   // untouched
        (await svc.GetEventsAsync(runId, 0, CancellationToken.None)).ShouldBeEmpty();                  // the harness was never spawned
    }

    [Fact]
    public async Task Nonzero_harness_exit_completes_failed()
    {
        if (OperatingSystem.IsWindows()) return;

        var teamId = await SeedTeamAsync();
        var runId = await CreateScriptedRunAsync(teamId);

        await ExecuteAsync(runId, new ScriptedHarness("printf 'working\\n'; exit 7"));

        using var verify = _fixture.BeginScope();
        (await verify.Resolve<IAgentRunService>().GetAsync(runId, CancellationToken.None)).Status.ShouldBe(AgentRunStatus.Failed);
    }

    [Fact]
    public async Task Sandbox_timeout_completes_timed_out()
    {
        if (OperatingSystem.IsWindows()) return;

        var teamId = await SeedTeamAsync();
        var runId = await CreateScriptedRunAsync(teamId, timeoutSeconds: 1);

        await ExecuteAsync(runId, new ScriptedHarness("sleep 10"));

        using var verify = _fixture.BeginScope();
        (await verify.Resolve<IAgentRunService>().GetAsync(runId, CancellationToken.None)).Status.ShouldBe(AgentRunStatus.TimedOut);
    }

    [Fact]
    public void Executor_is_registered_in_the_container()
    {
        using var scope = _fixture.BeginScope();
        scope.Resolve<IAgentRunExecutor>().ShouldNotBeNull();
    }

    [Fact]
    public async Task Clones_the_bound_repository_into_the_workspace_and_runs_there()
    {
        if (OperatingSystem.IsWindows()) return;
        if (!await GitAvailableAsync()) return;

        var teamId = await SeedTeamAsync();
        using var origin = new TempDir();
        await SeedLocalRepoAsync(origin.Path, "README.md", "hello-from-repo");
        var repoId = await SeedRepositoryAsync(teamId, new Uri(origin.Path).AbsoluteUri, defaultBranch: "main");
        var runId = await CreateRepoRunAsync(teamId, repoId);

        // The scripted harness `cat README.md` only sees the file if the executor cloned the repo AND
        // ran the harness with the clone as its working directory.
        await ExecuteAsync(runId, new ScriptedHarness("cat README.md"));

        using var scope = _fixture.BeginScope();
        var svc = scope.Resolve<IAgentRunService>();

        (await svc.GetAsync(runId, CancellationToken.None)).Status.ShouldBe(AgentRunStatus.Succeeded);
        (await svc.GetEventsAsync(runId, 0, CancellationToken.None)).Select(e => e.Text)
            .ShouldContain("hello-from-repo", "the harness ran inside the cloned workspace and read the repo file");
    }

    [Fact]
    public async Task Workspace_clone_failure_lands_the_run_failed_not_stuck()
    {
        if (OperatingSystem.IsWindows()) return;
        if (!await GitAvailableAsync()) return;

        var teamId = await SeedTeamAsync();
        // A repo whose clone URL points at a non-existent local path → the clone fails. The executor must
        // land the run Failed (its catch maps the WorkspaceException), never leave it stuck Running.
        var missing = new Uri(Path.Combine(Path.GetTempPath(), "cs-missing-" + Guid.NewGuid().ToString("N"))).AbsoluteUri;
        var repoId = await SeedRepositoryAsync(teamId, missing, "main");
        var runId = await CreateRepoRunAsync(teamId, repoId);

        await ExecuteAsync(runId, new ScriptedHarness("echo should-not-run"));

        using var verify = _fixture.BeginScope();
        var run = await verify.Resolve<IAgentRunService>().GetAsync(runId, CancellationToken.None);
        run.Status.ShouldBe(AgentRunStatus.Failed, "a workspace clone failure lands the run Failed, not stuck Running");
        run.Error.ShouldNotBeNull();
    }

    private async Task<Guid> CreateRepoRunAsync(Guid teamId, Guid repositoryId)
    {
        using var scope = _fixture.BeginScope();
        var run = await scope.Resolve<IAgentRunService>().CreateAsync(
            new AgentTask { Goal = "edit", Harness = "scripted", Model = "test-model", RepositoryId = repositoryId },
            teamId, null, null, CancellationToken.None);
        return run.Id;
    }

    private async Task<Guid> SeedRepositoryAsync(Guid teamId, string cloneUrlHttps, string defaultBranch)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        var instanceId = Guid.NewGuid();
        db.ProviderInstance.Add(new ProviderInstance { Id = instanceId, TeamId = teamId, Provider = ProviderKind.Git, DisplayName = "local", BaseUrl = "https://local" });

        var repoId = Guid.NewGuid();
        db.Repository.Add(new Repository
        {
            Id = repoId, TeamId = teamId, ProviderInstanceId = instanceId, CredentialId = null,
            ExternalId = repoId.ToString(), NamespacePath = "org", Name = "repo", FullPath = "org/repo",
            DefaultBranch = defaultBranch, CloneUrlHttps = cloneUrlHttps, WebUrl = "https://local/org/repo",
        });

        await db.SaveChangesAsync();
        return repoId;
    }

    private static async Task<bool> GitAvailableAsync()
    {
        try { return (await new LocalProcessRunner().RunAsync(new SandboxSpec { Command = "git", Args = new[] { "--version" }, TimeoutSeconds = 10 }, CancellationToken.None)).Status == SandboxStatus.Success; }
        catch { return false; }
    }

    private static async Task SeedLocalRepoAsync(string dir, string file, string content)
    {
        await RunGitInAsync(dir, "init", "-b", "main");
        await RunGitInAsync(dir, "config", "user.email", "test@codespace.dev");
        await RunGitInAsync(dir, "config", "user.name", "Test");
        await RunGitInAsync(dir, "config", "commit.gpgsign", "false");
        await File.WriteAllTextAsync(Path.Combine(dir, file), content);
        await RunGitInAsync(dir, "add", ".");
        await RunGitInAsync(dir, "commit", "-m", "seed");
    }

    private static async Task RunGitInAsync(string workdir, params string[] args)
    {
        var result = await new LocalProcessRunner().RunAsync(
            new SandboxSpec { Command = "git", Args = args, WorkingDirectory = workdir, TimeoutSeconds = 60 }, CancellationToken.None);
        if (result.Status != SandboxStatus.Success) throw new InvalidOperationException($"git {string.Join(' ', args)} failed: {result.Stderr}");
    }

    private sealed class TempDir : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "cs-agent-origin-" + Guid.NewGuid().ToString("N"));
        public TempDir() => Directory.CreateDirectory(Path);
        public void Dispose() { try { Directory.Delete(Path, recursive: true); } catch { /* best-effort */ } }
    }

    [Fact]
    public async Task Heartbeat_loop_advances_heartbeat_during_a_long_quiet_run()
    {
        if (OperatingSystem.IsWindows()) return;

        // Force the heartbeat cadence to its 5s floor (window/3 of 9s → floored 5s) so a ping lands during
        // the ~8s run. The harness sleeps emitting NO events, so ONLY the heartbeat loop — pinging through
        // its dedicated-scope DbContext concurrently with the (empty) event stream — can advance HeartbeatAt.
        var original = Environment.GetEnvironmentVariable(AgentRunLiveness.WindowEnvVar);
        try
        {
            Environment.SetEnvironmentVariable(AgentRunLiveness.WindowEnvVar, "00:00:09");

            var teamId = await SeedTeamAsync();
            var runId = await CreateScriptedRunAsync(teamId);

            await ExecuteAsync(runId, new ScriptedHarness("sleep 8"));

            using var scope = _fixture.BeginScope();
            var run = await scope.Resolve<IAgentRunService>().GetAsync(runId, CancellationToken.None);

            run.StartedAt.ShouldNotBeNull();
            run.HeartbeatAt.ShouldNotBeNull();
            // MarkRunning stamps StartedAt ≈ HeartbeatAt at claim; a ≥3s advance can only come from a mid-run
            // ping (the 5s-floor interval fired before the 8s run ended). Regressing to the shared _runs
            // context — or dropping the loop — would leave HeartbeatAt at its claim value and fail this.
            (run.HeartbeatAt!.Value - run.StartedAt!.Value).ShouldBeGreaterThan(TimeSpan.FromSeconds(3));
        }
        finally
        {
            Environment.SetEnvironmentVariable(AgentRunLiveness.WindowEnvVar, original);
        }
    }

    private async Task ExecuteAsync(Guid runId, IAgentHarness harness)
    {
        using var scope = _fixture.BeginScope();
        var executor = new AgentRunExecutor(
            scope.Resolve<IAgentRunService>(),
            new AgentHarnessRegistry(new[] { harness }),
            scope.Resolve<ISandboxRunnerRegistry>(),
            scope.Resolve<IAgentWorkspaceResolver>(),
            scope.Resolve<IWorkspaceProviderRegistry>(),
            scope.Resolve<IAgentRunCompletionNotifier>(),
            scope.Resolve<Microsoft.Extensions.DependencyInjection.IServiceScopeFactory>(),
            NullLogger<AgentRunExecutor>.Instance);

        await executor.ExecuteAsync(runId, CancellationToken.None);
    }

    private async Task<Guid> CreateScriptedRunAsync(Guid teamId, int timeoutSeconds = 1800)
    {
        using var scope = _fixture.BeginScope();
        var run = await scope.Resolve<IAgentRunService>().CreateAsync(
            new AgentTask { Goal = "scripted", Harness = "scripted", Model = "test-model", TimeoutSeconds = timeoutSeconds },
            teamId, null, null, CancellationToken.None);
        return run.Id;
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

    /// <summary>A CLI-less test harness: builds a /bin/sh invocation from a fixed script, wraps each stdout line as an assistant message, and folds the exit code.</summary>
    private sealed class ScriptedHarness : IAgentHarness
    {
        private readonly string _script;

        public ScriptedHarness(string script) => _script = script;

        public string Kind => "scripted";
        public string Version => "test";
        public IReadOnlyList<string> Models { get; } = new[] { "test-model" };

        public SandboxSpec BuildInvocation(AgentTask task) => new() { Command = "/bin/sh", Args = new[] { "-c", _script }, WorkingDirectory = task.WorkspaceDirectory, TimeoutSeconds = task.TimeoutSeconds };

        public AgentEvent? ParseEvent(string rawLine) =>
            string.IsNullOrWhiteSpace(rawLine) ? null : new AgentEvent { Kind = AgentEventKind.AssistantMessage, Text = rawLine.Trim() };

        public AgentRunResult BuildResult(IReadOnlyList<AgentEvent> events, int exitCode) =>
            exitCode == 0
                ? new AgentRunResult { Status = AgentRunStatus.Succeeded, ExitReason = "completed", Summary = events.Count > 0 ? events[^1].Text : null }
                : new AgentRunResult { Status = AgentRunStatus.Failed, ExitReason = "non-zero-exit", Error = $"exit {exitCode}" };
    }
}
