using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Agents.Sandbox;
using CodeSpace.Core.Services.Agents.Sandbox.Runners;
using CodeSpace.Core.Services.Supervisor;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Agents.Benchmark;
using CodeSpace.Messages.Enums;
using Shouldly;

namespace CodeSpace.IntegrationTests.Agents;

/// <summary>
/// 🟢 Integration (high-fidelity real-git, Rule 9): the supervisor's objective acceptance gate through the WHOLE real
/// stack — the DI-resolved <see cref="SupervisorAcceptanceGrader"/> → real <c>RepositoryWorkspaceResolver</c> (a
/// Postgres-seeded <see cref="Repository"/> row) → real <c>LocalGitWorkspaceProvider</c> (a real shallow clone of a
/// real branch on a file:// bare remote) → real <see cref="Core.Services.Agents.Eval.Benchmark.Graders.TestsPassGrader"/>
/// running a real <c>check.sh</c> through the real <c>LocalProcessRunner</c>. No agent runs and no self-report is
/// consulted: the verdict is PURELY the exit code of the cloned branch's own check — a branch whose check exits 0
/// is accepted, one whose check exits 1 is not, and a branch that cannot be cloned fails CLOSED. POSIX-only:
/// the check + git run as real processes. Real Postgres + a hermetic local git remote (no external network) ⇒
/// Integration tier, not E2E (see backend/tests/TESTING.md).
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public sealed class SupervisorAcceptanceGradeFlowTests
{
    private readonly PostgresFixture _fixture;

    public SupervisorAcceptanceGradeFlowTests(PostgresFixture fixture) { _fixture = fixture; }

    [Fact]
    public async Task A_branch_whose_check_passes_is_accepted_and_a_failing_branch_is_not()
    {
        if (!await GitReadyAsync()) return;

        using var remote = new AcceptanceRemote();
        await remote.InitAsync();
        await remote.AddBranchWithCheckAsync("acc/pass", checkExitCode: 0);
        await remote.AddBranchWithCheckAsync("acc/fail", checkExitCode: 1);

        var teamId = await SeedTeamAsync();
        var repoId = await SeedBoundRepositoryAsync(teamId, remote.Url, "main");

        var pass = await GradeAsync(repoId, teamId, "acc/pass");
        pass.Passed.ShouldBeTrue("the cloned branch's check exits 0 → objectively accepted");
        pass.Detail.ShouldBe("tests-passed");

        var fail = await GradeAsync(repoId, teamId, "acc/fail");
        fail.Passed.ShouldBeFalse("the cloned branch's check exits 1 → not accepted, by the branch's own tests not any self-report");
        fail.Detail.ShouldBe("tests-failed-exit-1");
    }

    [Fact]
    public async Task The_verdict_is_independent_of_the_agent_flipping_only_with_the_branch()
    {
        if (!await GitReadyAsync()) return;

        using var remote = new AcceptanceRemote();
        await remote.InitAsync();
        // The SAME repo, SAME team, SAME command — only the branch's check.sh differs. No agent ever ran; the
        // verdict must track the branch's start-state alone (the honesty property the grade exists to enforce).
        await remote.AddBranchWithCheckAsync("a", checkExitCode: 0);
        await remote.AddBranchWithCheckAsync("b", checkExitCode: 1);

        var teamId = await SeedTeamAsync();
        var repoId = await SeedBoundRepositoryAsync(teamId, remote.Url, "main");

        (await GradeAsync(repoId, teamId, "a")).Passed.ShouldBeTrue();
        (await GradeAsync(repoId, teamId, "b")).Passed.ShouldBeFalse();
    }

    [Fact]
    public async Task A_check_that_hangs_past_the_timeout_grades_timed_out()
    {
        if (!await GitReadyAsync()) return;

        using var remote = new AcceptanceRemote();
        await remote.InitAsync();
        await remote.AddBranchWithScriptAsync("acc/hang", "#!/bin/sh\nsleep 30\n");

        var teamId = await SeedTeamAsync();
        var repoId = await SeedBoundRepositoryAsync(teamId, remote.Url, "main");

        var grade = await GradeAsync(repoId, teamId, "acc/hang", timeoutSeconds: 1);

        grade.Passed.ShouldBeFalse("a check that hangs is not accepted");
        grade.Detail.ShouldBe("tests-timed-out", "the model-authored timeout is enforced and the outcome is distinct from a plain exit-1 fail");
    }

    [Fact]
    public async Task A_command_that_cannot_run_fails_closed_instead_of_crashing()
    {
        if (!await GitReadyAsync()) return;

        using var remote = new AcceptanceRemote();
        await remote.InitAsync();
        await remote.AddBranchWithCheckAsync("acc/pass", checkExitCode: 0);

        var teamId = await SeedTeamAsync();
        var repoId = await SeedBoundRepositoryAsync(teamId, remote.Url, "main");

        // A model-authored command naming a binary that isn't on PATH surfaces as a process-start failure; it must NOT
        // crash the supervisor turn — acceptance can't be verified, so it fails closed to "not accepted".
        var grade = await GradeAsync(repoId, teamId, "acc/pass", command: new[] { "codespace-no-such-binary-xyz" });

        grade.Passed.ShouldBeFalse("a check that cannot even start is not a silent pass");
        grade.Detail.ShouldContain("grade-error");
    }

    [Fact]
    public async Task P3_1_a_contract_authored_timeout_overrides_the_servers_default_through_the_real_executor()
    {
        // P3.1: SupervisorAcceptanceSpec.TimeoutSeconds — a contract author's SHORT window must reach the real
        // grading call through AgentRunExecutor.GradeAcceptanceIfPresentAsync, not just SupervisorAcceptanceGrader
        // directly (which always took an explicit timeoutSeconds argument and so already "worked" in isolation).
        // Here a 1s contract timeout catches a 3s hang that the server's own (now 300s) default would sail past.
        if (!await GitReadyAsync()) return;

        using var remote = new AcceptanceRemote();
        await remote.InitAsync();
        await remote.AddBranchWithScriptAsync("acc/slow", "#!/bin/sh\nsleep 3\n");

        var teamId = await SeedTeamAsync();
        var repoId = await SeedBoundRepositoryAsync(teamId, remote.Url, "main");

        using var scope = _fixture.BeginScope();
        var harness = new NoOpHarness();
        var executor = new CodeSpace.Core.Services.Agents.AgentRunExecutor(
            scope.Resolve<CodeSpace.Core.Services.Agents.IAgentRunService>(),
            new CodeSpace.Core.Services.Agents.AgentHarnessRegistry(new CodeSpace.Core.Services.Agents.IAgentHarness[] { harness }),
            new CodeSpace.Core.Services.Agents.HarnessModelReconciler(new CodeSpace.Core.Services.Agents.AgentHarnessRegistry(new CodeSpace.Core.Services.Agents.IAgentHarness[] { harness }), scope.Resolve<CodeSpace.Core.Services.Agents.ModelCredentials.IModelPoolSelector>(), scope.Resolve<CodeSpaceDbContext>()),
            scope.Resolve<CodeSpace.Core.Services.Agents.Sandbox.ISandboxRunnerRegistry>(),
            scope.Resolve<CodeSpace.Core.Services.Agents.Workspace.IAgentWorkspaceResolver>(),
            scope.Resolve<CodeSpace.Core.Services.Agents.IModelCredentialResolver>(),
            scope.Resolve<CodeSpace.Core.Services.Agents.Workspace.IWorkspaceProviderRegistry>(),
            scope.Resolve<CodeSpace.Core.Services.Agents.IAgentRunCompletionNotifier>(),
            scope.Resolve<Microsoft.Extensions.DependencyInjection.IServiceScopeFactory>(),
            scope.Resolve<CodeSpaceDbContext>(),
            scope.Resolve<CodeSpace.Core.Services.Review.IStructuredCritic>(),
            scope.Resolve<CodeSpace.Core.Services.Workflows.Artifacts.IArtifactOffloader>(),
            scope.Resolve<CodeSpace.Core.Services.Agents.Publish.IPublishManifestStore>(),
            Array.Empty<CodeSpace.Core.Services.Agents.Publish.IPublishGuard>(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<CodeSpace.Core.Services.Agents.AgentRunExecutor>.Instance);

        var task = new AgentTask
        {
            Goal = "g", Harness = "no-op", Model = "test-model", RepositoryId = repoId,
            Acceptance = new SupervisorAcceptanceSpec { Command = new[] { "sh", "check.sh" }, TimeoutSeconds = 1 },
        };
        var run = new AgentRun { Id = Guid.NewGuid(), TeamId = teamId, Status = AgentRunStatus.Succeeded, TaskJson = System.Text.Json.JsonSerializer.Serialize(task, CodeSpace.Core.Services.Agents.AgentJson.Options) };
        var succeeded = new AgentRunResult { Status = AgentRunStatus.Succeeded, ExitReason = "completed", ProducedBranch = "acc/slow" };

        var graded = await executor.GradeAcceptanceIfPresentAsync(run, task, succeeded, CancellationToken.None);

        graded.AcceptancePassed.ShouldBe(false);
        graded.AcceptanceDetail.ShouldBe("tests-timed-out", "the 1s contract-authored timeout fired — the server's 300s default would have let the 3s sleep finish and pass");
    }

    [Fact]
    public async Task A_multi_repo_result_is_graded_per_repo_through_the_real_executor_not_left_ungraded()
    {
        // Before this fix, AgentRunExecutor.GradeAcceptanceIfPresentAsync deferred ENTIRELY on RepositoryResults.Count > 0
        // — a contract-bearing multi-repo single-agent run landed Succeeded with AcceptancePassed left null, self-report
        // only. Two REAL remotes (one per repo) prove the fix through the whole real DI-resolved executor + grader +
        // git stack, not a fake — one repo's branch passes its check, the other's fails, and the run must fail closed.
        if (!await GitReadyAsync()) return;

        using var web = new AcceptanceRemote();
        await web.InitAsync();
        await web.AddBranchWithCheckAsync("agent/web", checkExitCode: 0);

        using var api = new AcceptanceRemote();
        await api.InitAsync();
        await api.AddBranchWithCheckAsync("agent/api", checkExitCode: 1);

        var teamId = await SeedTeamAsync();
        var webRepoId = await SeedBoundRepositoryAsync(teamId, web.Url, "main");
        var apiRepoId = await SeedBoundRepositoryAsync(teamId, api.Url, "main");

        using var scope = _fixture.BeginScope();
        var harness = new NoOpHarness();
        var executor = new CodeSpace.Core.Services.Agents.AgentRunExecutor(
            scope.Resolve<CodeSpace.Core.Services.Agents.IAgentRunService>(),
            new CodeSpace.Core.Services.Agents.AgentHarnessRegistry(new CodeSpace.Core.Services.Agents.IAgentHarness[] { harness }),
            new CodeSpace.Core.Services.Agents.HarnessModelReconciler(new CodeSpace.Core.Services.Agents.AgentHarnessRegistry(new CodeSpace.Core.Services.Agents.IAgentHarness[] { harness }), scope.Resolve<CodeSpace.Core.Services.Agents.ModelCredentials.IModelPoolSelector>(), scope.Resolve<CodeSpaceDbContext>()),
            scope.Resolve<CodeSpace.Core.Services.Agents.Sandbox.ISandboxRunnerRegistry>(),
            scope.Resolve<CodeSpace.Core.Services.Agents.Workspace.IAgentWorkspaceResolver>(),
            scope.Resolve<CodeSpace.Core.Services.Agents.IModelCredentialResolver>(),
            scope.Resolve<CodeSpace.Core.Services.Agents.Workspace.IWorkspaceProviderRegistry>(),
            scope.Resolve<CodeSpace.Core.Services.Agents.IAgentRunCompletionNotifier>(),
            scope.Resolve<Microsoft.Extensions.DependencyInjection.IServiceScopeFactory>(),
            scope.Resolve<CodeSpaceDbContext>(),
            scope.Resolve<CodeSpace.Core.Services.Review.IStructuredCritic>(),
            scope.Resolve<CodeSpace.Core.Services.Workflows.Artifacts.IArtifactOffloader>(),
            scope.Resolve<CodeSpace.Core.Services.Agents.Publish.IPublishManifestStore>(),
            Array.Empty<CodeSpace.Core.Services.Agents.Publish.IPublishGuard>(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<CodeSpace.Core.Services.Agents.AgentRunExecutor>.Instance);

        var task = new AgentTask
        {
            Goal = "g", Harness = "no-op", Model = "test-model",
            Acceptance = new SupervisorAcceptanceSpec { Command = new[] { "sh", "check.sh" } },
        };
        var run = new AgentRun { Id = Guid.NewGuid(), TeamId = teamId, Status = AgentRunStatus.Succeeded, TaskJson = System.Text.Json.JsonSerializer.Serialize(task, CodeSpace.Core.Services.Agents.AgentJson.Options) };
        var succeeded = new AgentRunResult
        {
            Status = AgentRunStatus.Succeeded,
            ExitReason = "completed",
            RepositoryResults = new[]
            {
                new RepositoryRunResult { Alias = "web", RepositoryId = webRepoId, ProducedBranch = "agent/web" },
                new RepositoryRunResult { Alias = "api", RepositoryId = apiRepoId, ProducedBranch = "agent/api" },
            },
        };

        var graded = await executor.GradeAcceptanceIfPresentAsync(run, task, succeeded, CancellationToken.None);

        graded.Status.ShouldBe(AgentRunStatus.Failed, "the 'api' repo's real check exits 1 — a contract binds the WHOLE multi-repo change, so the run must fail closed rather than stay Succeeded on self-report");
        graded.AcceptancePassed.ShouldBe(false);
        graded.AcceptanceDetail.ShouldBe("repo 'api': tests-failed-exit-1");
    }

    [Fact]
    public async Task P3_2_a_setup_command_runs_before_the_check_in_the_same_real_workspace()
    {
        // The check only exits 0 if a file the SETUP step creates already exists — proving both that setup runs
        // (not skipped) and that it runs BEFORE the check, in the same cloned workspace.
        if (!await GitReadyAsync()) return;

        using var remote = new AcceptanceRemote();
        await remote.InitAsync();
        await remote.AddBranchWithScriptAsync("acc/needs-setup", "#!/bin/sh\ntest -f setup-marker.txt\n");

        var teamId = await SeedTeamAsync();
        var repoId = await SeedBoundRepositoryAsync(teamId, remote.Url, "main");

        var grade = await GradeAsync(repoId, teamId, "acc/needs-setup", setupCommand: new[] { "sh", "-c", "echo ok > setup-marker.txt" });

        grade.Passed.ShouldBeTrue("the setup step created the marker file the check requires, before the check ran");
        grade.Detail.ShouldBe("tests-passed");
    }

    [Fact]
    public async Task P3_2_a_failing_setup_command_fails_closed_and_is_infra_classified()
    {
        if (!await GitReadyAsync()) return;

        using var remote = new AcceptanceRemote();
        await remote.InitAsync();
        // The check would trivially pass if reached — proving the setup failure short-circuits BEFORE the check runs.
        await remote.AddBranchWithCheckAsync("acc/setup-fails", checkExitCode: 0);

        var teamId = await SeedTeamAsync();
        var repoId = await SeedBoundRepositoryAsync(teamId, remote.Url, "main");

        var grade = await GradeAsync(repoId, teamId, "acc/setup-fails", setupCommand: new[] { "sh", "-c", "exit 1" });

        grade.Passed.ShouldBeFalse("a failing setup means the check never got a chance to run");
        grade.Detail.ShouldStartWith("setup-failed:");
        CodeSpace.Core.Services.Agents.AgentAcceptanceContract.IsInfraFailure(grade.Detail, workPresent: true).ShouldBeTrue("the check machinery itself never functioned — infra, not a genuine failing verdict");
    }

    [Fact]
    public async Task P3_2_a_setup_command_that_hangs_past_the_timeout_grades_setup_timed_out()
    {
        if (!await GitReadyAsync()) return;

        using var remote = new AcceptanceRemote();
        await remote.InitAsync();
        await remote.AddBranchWithCheckAsync("acc/setup-hangs", checkExitCode: 0);

        var teamId = await SeedTeamAsync();
        var repoId = await SeedBoundRepositoryAsync(teamId, remote.Url, "main");

        var grade = await GradeAsync(repoId, teamId, "acc/setup-hangs", setupCommand: new[] { "sh", "-c", "sleep 30" }, timeoutSeconds: 1);

        grade.Passed.ShouldBeFalse();
        grade.Detail.ShouldBe("setup-timed-out", "distinct from a plain setup failure — the same distinction the check's own timeout draws");
        CodeSpace.Core.Services.Agents.AgentAcceptanceContract.IsInfraFailure(grade.Detail, workPresent: true).ShouldBeTrue();
    }

    [Fact]
    public async Task A_branch_that_cannot_be_cloned_fails_closed()
    {
        if (!await GitReadyAsync()) return;

        using var remote = new AcceptanceRemote();
        await remote.InitAsync();   // only main exists

        var teamId = await SeedTeamAsync();
        var repoId = await SeedBoundRepositoryAsync(teamId, remote.Url, "main");

        var grade = await GradeAsync(repoId, teamId, "does/not-exist");

        grade.Passed.ShouldBeFalse("a branch that cannot be cloned cannot be verified → not accepted (fail closed, never a silent pass)");
        grade.Detail.ShouldContain("clone-failed");
    }

    // ─── Helpers ───

    private async Task<BenchmarkGrade> GradeAsync(Guid repoId, Guid teamId, string branch, IReadOnlyList<string>? command = null, int timeoutSeconds = 60, IReadOnlyList<string>? setupCommand = null)
    {
        using var scope = _fixture.BeginScope();   // resolving the grader from DI proves it auto-registers
        return await scope.Resolve<ISupervisorAcceptanceGrader>()
            .GradeAsync(repoId, teamId, branch, new SupervisorAcceptanceSpec { Command = command ?? new[] { "sh", "check.sh" }, SetupCommand = setupCommand }, timeoutSeconds, CancellationToken.None);
    }

    private async Task<Guid> SeedTeamAsync()
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        var userId = Guid.NewGuid();
        db.User.Add(new User { Id = userId, Email = $"acc-{userId:N}@test.local", Name = $"acc-{userId:N}" });

        var teamId = Guid.NewGuid();
        db.Team.Add(new Team { Id = teamId, Slug = $"acc-{teamId:N}", Name = "Acceptance Team", Kind = TeamKind.Workspace, OwnerUserId = userId });
        db.TeamMembership.Add(new TeamMembership { Id = Guid.NewGuid(), TeamId = teamId, UserId = userId, Role = TeamRole.Owner });

        await db.SaveChangesAsync();
        return teamId;
    }

    private async Task<Guid> SeedBoundRepositoryAsync(Guid teamId, string cloneUrlHttps, string defaultBranch)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        var instanceId = Guid.NewGuid();
        db.ProviderInstance.Add(new ProviderInstance { Id = instanceId, TeamId = teamId, Provider = ProviderKind.GitHub, DisplayName = "local", BaseUrl = $"https://local/{instanceId:N}" });

        var serializer = scope.Resolve<CodeSpace.Core.Services.Credentials.ICredentialPayloadSerializer>();
        var encryptor = scope.Resolve<CodeSpace.Core.Services.Credentials.IPayloadEncryptor>();
        var payloadJson = serializer.Serialize(new CodeSpace.Messages.Credentials.PatPayload { Token = "integration-token" });

        var credentialId = Guid.NewGuid();
        db.Credential.Add(new Credential
        {
            Id = credentialId, TeamId = teamId, ProviderInstanceId = instanceId, AuthType = AuthType.Pat, DisplayName = "clone cred",
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

    private static async Task<bool> GitReadyAsync()
    {
        if (OperatingSystem.IsWindows()) return false;
        try { return (await new LocalProcessRunner().RunAsync(new SandboxSpec { Command = "git", Args = new[] { "--version" }, TimeoutSeconds = 10 }, CancellationToken.None)).Status == SandboxStatus.Success; }
        catch { return false; }
    }

    /// <summary>A bare file:// remote seeded with a main commit, to which acceptance branches each carrying a <c>check.sh</c> (whose exit code is the start-state) are pushed. The grader clones a branch and re-runs that check.</summary>
    private sealed class AcceptanceRemote : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "cs-sup-accept-" + Guid.NewGuid().ToString("N"));
        private readonly string _bare;
        private readonly string _seed;

        public AcceptanceRemote()
        {
            Directory.CreateDirectory(_root);
            _bare = Path.Combine(_root, "remote.git");
            _seed = Path.Combine(_root, "seed");
        }

        public string Url => new Uri(_bare).AbsoluteUri;

        public async Task InitAsync()
        {
            await Git(_root, "init", "--bare", "-b", "main", _bare);
            Directory.CreateDirectory(_seed);
            await Git(_seed, "clone", _bare, _seed);
            await Config(_seed);
            await File.WriteAllTextAsync(Path.Combine(_seed, "README.md"), "base\n");
            await Git(_seed, "add", "-A");
            await Git(_seed, "commit", "-m", "seed");
            await Git(_seed, "push", "origin", "main");
        }

        public Task AddBranchWithCheckAsync(string branch, int checkExitCode) =>
            AddBranchWithScriptAsync(branch, $"#!/bin/sh\nexit {checkExitCode}\n");

        public async Task AddBranchWithScriptAsync(string branch, string scriptBody)
        {
            await Git(_seed, "checkout", "-B", branch, "main");
            await File.WriteAllTextAsync(Path.Combine(_seed, "check.sh"), scriptBody);
            await Git(_seed, "add", "-A");
            await Git(_seed, "commit", "-m", $"check on {branch}");
            await Git(_seed, "push", "origin", branch);
            await Git(_seed, "checkout", "main");
        }

        private static async Task Config(string dir)
        {
            await Git(dir, "config", "user.email", "test@codespace.dev");
            await Git(dir, "config", "user.name", "Test");
            await Git(dir, "config", "commit.gpgsign", "false");
        }

        private static async Task Git(string workdir, params string[] args)
        {
            var result = await new LocalProcessRunner().RunAsync(new SandboxSpec { Command = "git", Args = args, WorkingDirectory = workdir, TimeoutSeconds = 60 }, CancellationToken.None);
            if (result.Status != SandboxStatus.Success) throw new InvalidOperationException($"git {string.Join(' ', args)} failed (exit {result.ExitCode}): {result.Stderr}");
        }

        public void Dispose()
        {
            try { Directory.Delete(_root, recursive: true); } catch { /* best-effort */ }
        }
    }

    /// <summary>A harness that is never actually invoked — GradeAcceptanceIfPresentAsync never touches the harness/workspace machinery, this only satisfies AgentRunExecutor's constructor.</summary>
    private sealed class NoOpHarness : CodeSpace.Core.Services.Agents.IAgentHarness
    {
        public string Kind => "no-op";
        public string Version => "test";
        public IReadOnlyList<string> Models { get; } = new[] { "test-model" };

        public SandboxSpec BuildInvocation(AgentTask task) => throw new NotSupportedException();
        public IReadOnlyList<CodeSpace.Messages.Agents.AgentEvent> ParseEvents(string rawLine) => throw new NotSupportedException();
        public AgentRunResult BuildResult(IReadOnlyList<CodeSpace.Messages.Agents.AgentEvent> events, int exitCode) => throw new NotSupportedException();
    }
}
