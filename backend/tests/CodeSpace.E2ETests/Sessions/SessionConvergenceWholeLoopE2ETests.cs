using System.Text.Json;
using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Agents.Sandbox;
using CodeSpace.Core.Services.Agents.Sandbox.Runners;
using CodeSpace.Core.Services.Supervisor;
using CodeSpace.Core.Services.Tasks;
using CodeSpace.Core.Services.Workflows.Engine;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.IntegrationTests.Infrastructure.Jobs;
using CodeSpace.IntegrationTests.Workflows.Infrastructure;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Agents.Benchmark;
using CodeSpace.Messages.Commands.Tasks;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Credentials;
using CodeSpace.Messages.Enums;
using CodeSpace.Messages.Tasks;
using CodeSpace.Messages.Tasks.Effort;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace CodeSpace.E2ETests.Sessions;

/// <summary>
/// 🟢 THE session multi-turn CONVERGENCE gate — the join the SOTA assessment found missing: every prior multi-turn
/// proof either stubbed the agent's coding (so it proved the brain DROVE the arc, never that work SOLVED + carried) or
/// stopped at a single turn-2 plan decision. This drives TWO real session turns through the production launch + durable
/// engine + a real OS-process agent that EDITS files in its cloned workspace, and proves OBJECTIVELY that turn 2 built
/// ON turn 1's produced code, not from scratch:
/// <list type="number">
///   <item>Turn 1 (a real launch) implements feature A and the executor pushes a real produced branch.</item>
///   <item>Turn 2 (a CONTINUE) resolves its clone to turn 1's PRODUCED branch (asserted on the frozen agent
///         <c>baseRef</c>), implements feature B, and writes NOTHING for A.</item>
///   <item>The real <see cref="ISupervisorAcceptanceGrader"/> re-clones turn 2's head and runs a both-features
///         <c>check.sh</c> output-equality oracle: A (written ONLY on turn 1) AND B (written ONLY on turn 2) both pass —
///         so A survived solely because turn 2 carried turn 1's branch forward. That is convergence.</item>
/// </list>
///
/// <para><b>The teeth.</b> A second arm runs a CLI that DISCARDS the prior turn's work (clobbers <c>a.sh</c> while doing
/// B) — the both-features oracle then goes RED, proving the grade measures "built on the carried branch", not "some
/// branch was produced".</para>
///
/// <para>Tier: 🟢 high-fidelity E2E (Surface=Engine) — real launch service + real <see cref="IWorkflowEngine"/> + real
/// <see cref="AgentRunExecutor"/> + real <c>LocalProcessRunner</c> editing a real cloned git workspace + real push to a
/// bare <c>file://</c> remote + the real objective acceptance grader, over real Postgres. ALWAYS-ON (no model secrets —
/// the convergence mechanics are deterministic). Skips on Windows / when git is absent (the fake CLI is a /bin/sh
/// script). The live-model coding arm — a real CLI authoring A then B across turns — is the gated follow-up.</para>
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "E2E")]
[Trait("Surface", "Engine")]
public sealed class SessionConvergenceWholeLoopE2ETests : IDisposable
{
    private const string AlphaGoal = "Implement " + ConvergenceFeatureFakeCli.AlphaMarker + ": add a.sh that prints A-OK.";
    private const string BetaGoal = "Now implement " + ConvergenceFeatureFakeCli.BetaMarker + ": add b.sh that prints B-OK. Leave a.sh unchanged.";

    private readonly PostgresFixture _fixture;
    private readonly string? _pushBefore;

    public SessionConvergenceWholeLoopE2ETests(PostgresFixture fixture)
    {
        _fixture = fixture;

        // A task-launch single-agent run pushes its produced branch only when the deployment push flag is on (the launch
        // projection omits a per-run pushBranch, so ShouldPushProducedBranch defers to this env var). Turn 2 can only
        // build on turn 1's branch if turn 1 actually PUSHED it to the bound remote.
        _pushBefore = Environment.GetEnvironmentVariable(AgentRunExecutor.PushEnabledEnvVar);
        Environment.SetEnvironmentVariable(AgentRunExecutor.PushEnabledEnvVar, "1");
    }

    public void Dispose() => Environment.SetEnvironmentVariable(AgentRunExecutor.PushEnabledEnvVar, _pushBefore);

    [Fact]
    public async Task Turn2_builds_feature_B_on_turn1s_produced_branch_and_both_features_pass_the_objective_grade()
    {
        if (OperatingSystem.IsWindows()) return;   // the fake CLI is a /bin/sh script the runner spawns
        if (!await GitReadyAsync()) return;         // real git is required for clone / capture / push / grade

        using var cli = new ConvergenceFeatureFakeCli(preservePriorWork: true);

        var jobClient = ResolveJobClient();
        jobClient.Clear();
        jobClient.AutoExecute = true;   // the agent.code suspend runs the REAL executor + runner + fake CLI

        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);

        using var remote = new BareRemote();
        await remote.SeedBaseAsync(new() { ["check.sh"] = ConvergenceFeatureFakeCli.BothFeaturesCheckSh, ["base.txt"] = "base\n" });
        var repoId = await SeedBoundRepositoryAsync(teamId, remote.Url, "main");

        // ── TURN 1: implement feature A; the executor pushes a real produced branch to the bound remote. ──
        var (turn1Branch, sessionId) = await RunFreshTurnAsync(teamId, userId, repoId, AlphaGoal);
        (await remote.RemoteHasBranchAsync(turn1Branch)).ShouldBeTrue("turn 1's produced branch is on the remote — the base turn 2 must build on");

        // ── TURN 2: a CONTINUE — must clone turn 1's PRODUCED branch and add feature B without touching A. ──
        var second = await LaunchAsync(ContinueRequest(teamId, userId, sessionId, repoId, BetaGoal));

        (await ReadAgentBaseRefAsync(second.RunId)).ShouldBe(turn1Branch,
            "the continue resolved turn 2's clone to turn 1's PRODUCED branch — code carries forward, not base");

        await ExecuteAsync(second.RunId, jobClient);
        var secondRun = await LoadRunAsync(second.RunId);
        secondRun.Status.ShouldBe(WorkflowRunStatus.Success, "turn 2 must complete and push the carried-forward branch");
        var turn2Branch = ReadProducedBranch(secondRun);
        turn2Branch.ShouldNotBeNullOrEmpty("turn 2 pushed its produced branch");

        // ── OBJECTIVE CONVERGENCE PROOF: A (written ONLY on turn 1) AND B (written ONLY on turn 2) both pass on turn 2's
        //    head. A is present solely because turn 2 carried turn 1's branch forward — the real grader confirms it. ──
        var grade = await GradeAsync(repoId, teamId, turn2Branch!);
        grade.Passed.ShouldBeTrue($"both features must pass on the carried-forward head — convergence. Grader detail: {grade.Detail}");
    }

    [Fact]
    public async Task A_turn2_that_discards_the_prior_branch_fails_the_both_features_oracle()
    {
        if (OperatingSystem.IsWindows()) return;
        if (!await GitReadyAsync()) return;

        // The teeth: this CLI implements B but CLOBBERS a.sh — simulating an agent that discarded / redid the prior turn's
        // work. Even though turn 2 still clones turn 1's branch, the both-features oracle must catch the lost feature A.
        using var cli = new ConvergenceFeatureFakeCli(preservePriorWork: false);

        var jobClient = ResolveJobClient();
        jobClient.Clear();
        jobClient.AutoExecute = true;

        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);

        using var remote = new BareRemote();
        await remote.SeedBaseAsync(new() { ["check.sh"] = ConvergenceFeatureFakeCli.BothFeaturesCheckSh, ["base.txt"] = "base\n" });
        var repoId = await SeedBoundRepositoryAsync(teamId, remote.Url, "main");

        var (turn1Branch, sessionId) = await RunFreshTurnAsync(teamId, userId, repoId, AlphaGoal);
        (await remote.RemoteHasBranchAsync(turn1Branch)).ShouldBeTrue("turn 1 pushed feature A's branch — so the loss below is the agent's clobber, NOT a missing-branch soft fallback to main");

        var second = await LaunchAsync(ContinueRequest(teamId, userId, sessionId, repoId, BetaGoal));
        (await ReadAgentBaseRefAsync(second.RunId)).ShouldBe(turn1Branch, "turn 2 still carries turn 1's branch — the loss is the agent's, not the seam's");

        await ExecuteAsync(second.RunId, jobClient);
        var turn2Branch = ReadProducedBranch(await LoadRunAsync(second.RunId));
        turn2Branch.ShouldNotBeNullOrEmpty();

        var grade = await GradeAsync(repoId, teamId, turn2Branch!);
        grade.Passed.ShouldBeFalse("turn 2 clobbered feature A, so the both-features oracle must REJECT the head — the grade has teeth");
        // Pin the REASON: a genuine oracle rejection (check.sh exit 1) is "tests-failed-…"; a fail-closed plumbing error
        // (clone-failed / grade-error / no-workspace / no-test-command) would ALSO be Passed==false, so without this the
        // teeth could rot silently into a green that proved nothing. (Shouldly reports the actual Detail on mismatch.)
        grade.Detail.ShouldStartWith("tests-failed");
    }

    // ── Flow helpers ───────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Launch a FRESH turn (opens a new session), run it to completion, assert success, and return its produced branch + the session it opened.</summary>
    private async Task<(string Branch, Guid SessionId)> RunFreshTurnAsync(Guid teamId, Guid userId, Guid repoId, string goal)
    {
        var result = await LaunchAsync(new TaskLaunchRequest
        {
            TeamId = teamId, ActorUserId = userId, SurfaceKind = TaskLaunchSurfaceKinds.Chat,
            TaskText = goal, RepositoryId = repoId, RequestedEffort = TaskEffortModes.Quick, Autonomy = "Confined",
            Overrides = new TaskExecutionOverrides { Harness = "codex-cli", RunnerKind = "local" },
        });

        await ExecuteAsync(result.RunId, ResolveJobClient());

        var run = await LoadRunAsync(result.RunId);
        run.Status.ShouldBe(WorkflowRunStatus.Success, "the turn must complete so its produced branch exists to carry forward");

        var branch = ReadProducedBranch(run);
        branch.ShouldNotBeNullOrEmpty("the turn pushed a produced branch (push enabled + bound repo)");
        return (branch!, result.SessionId);
    }

    /// <summary>Kick the initial engine pass (AutoExecute chains only the SUBSEQUENT suspend/dispatch/resume jobs), then drain.</summary>
    private async Task ExecuteAsync(Guid runId, InMemoryBackgroundJobClient jobClient)
    {
        await RunEngineAsync(runId);
        await jobClient.WaitForPendingAsync();
    }

    private async Task<BenchmarkGrade> GradeAsync(Guid repoId, Guid teamId, string branch)
    {
        using var scope = _fixture.BeginScope();
        return await scope.Resolve<ISupervisorAcceptanceGrader>().GradeAsync(repoId, teamId, branch, new SupervisorAcceptanceSpec { Command = new[] { "sh", "check.sh" } }, 60, CancellationToken.None);
    }

    private static TaskLaunchRequest ContinueRequest(Guid teamId, Guid userId, Guid sessionId, Guid repoId, string text) => new()
    {
        TeamId = teamId, ActorUserId = userId, SurfaceKind = TaskLaunchSurfaceKinds.Chat,
        TaskText = text, ContinueSessionId = sessionId, RepositoryId = repoId,
        RequestedEffort = TaskEffortModes.Quick, Autonomy = "Confined",
        Overrides = new TaskExecutionOverrides { Harness = "codex-cli", RunnerKind = "local" },
    };

    /// <summary>The flat single-repo produced branch the terminal binds onto OutputsJson (<c>branch</c>) — the source SessionBranchResolver reads.</summary>
    private static string? ReadProducedBranch(WorkflowRun run)
    {
        if (string.IsNullOrWhiteSpace(run.OutputsJson)) return null;
        var root = JsonDocument.Parse(run.OutputsJson!).RootElement;
        return root.TryGetProperty("branch", out var b) && b.ValueKind == JsonValueKind.String ? b.GetString() : null;
    }

    // ── DI / engine plumbing (mirror RealModelSessionWholeLoopE2ETests) ──────────────────────────────────────────────

    private async Task<LaunchTaskResult> LaunchAsync(TaskLaunchRequest request)
    {
        using var scope = _fixture.BeginScope();
        return await scope.Resolve<ITaskLaunchService>().LaunchAsync(request, CancellationToken.None);
    }

    private async Task RunEngineAsync(Guid runId)
    {
        using var scope = _fixture.BeginScope();
        await scope.Resolve<IWorkflowEngine>().ExecuteRunAsync(runId, CancellationToken.None);
    }

    private InMemoryBackgroundJobClient ResolveJobClient()
    {
        using var scope = _fixture.BeginScope();
        return scope.Resolve<InMemoryBackgroundJobClient>();
    }

    private async Task<WorkflowRun> LoadRunAsync(Guid runId)
    {
        using var scope = _fixture.BeginScope();
        return await scope.Resolve<CodeSpaceDbContext>().WorkflowRun.AsNoTracking().SingleAsync(r => r.Id == runId);
    }

    /// <summary>Reads the projected agent.code node's <c>baseRef</c> input out of the frozen definition snapshot (null ⇒ default branch). Mirrors WorkSessionBranchFlowTests.</summary>
    private async Task<string?> ReadAgentBaseRefAsync(Guid runId)
    {
        var run = await LoadRunAsync(runId);
        var root = JsonDocument.Parse(run.DefinitionSnapshotJson!).RootElement;
        var agent = root.GetProperty("nodes").EnumerateArray().Single(n => n.GetProperty("id").GetString() == "agent");
        return agent.GetProperty("inputs").TryGetProperty("baseRef", out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    }

    private static async Task<bool> GitReadyAsync()
    {
        if (OperatingSystem.IsWindows()) return false;
        try { return (await new LocalProcessRunner().RunAsync(new SandboxSpec { Command = "git", Args = new[] { "--version" }, TimeoutSeconds = 10 }, CancellationToken.None)).Status == SandboxStatus.Success; }
        catch { return false; }
    }

    private async Task<Guid> SeedBoundRepositoryAsync(Guid teamId, string cloneUrlHttps, string defaultBranch)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        var instanceId = Guid.NewGuid();
        db.ProviderInstance.Add(new ProviderInstance { Id = instanceId, TeamId = teamId, Provider = ProviderKind.GitHub, DisplayName = "local", BaseUrl = $"https://local/{instanceId:N}" });

        var serializer = scope.Resolve<CodeSpace.Core.Services.Credentials.ICredentialPayloadSerializer>();
        var encryptor = scope.Resolve<CodeSpace.Core.Services.Credentials.IPayloadEncryptor>();
        var payloadJson = serializer.Serialize(new PatPayload { Token = "integration-token" });

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

    /// <summary>A bare local repo standing in for the remote — base-seeding + ref inspection. GUID-suffixed; best-effort cleaned. Mirrors SupervisorWholeLoopE2ETests.BareRemote.</summary>
    private sealed class BareRemote : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "cs-session-converge-" + Guid.NewGuid().ToString("N"));
        private readonly string _bare;

        public BareRemote()
        {
            Directory.CreateDirectory(_root);
            _bare = Path.Combine(_root, "remote.git");
        }

        public string Url => new Uri(_bare).AbsoluteUri;

        public async Task SeedBaseAsync(Dictionary<string, string> files)
        {
            await Git(_root, "init", "--bare", "-b", "main", _bare);
            var seed = Path.Combine(_root, "seed");
            Directory.CreateDirectory(seed);
            await Git(seed, "clone", _bare, seed);
            await Config(seed);
            foreach (var (name, content) in files) await File.WriteAllTextAsync(Path.Combine(seed, name), content);
            await Git(seed, "add", "-A");
            await Git(seed, "commit", "-m", "seed");
            await Git(seed, "push", "origin", "main");
        }

        public async Task<bool> RemoteHasBranchAsync(string branch) =>
            (await Git(_root, "--git-dir", _bare, "branch", "--list", branch)).Trim().Length > 0;

        private static async Task Config(string dir)
        {
            await Git(dir, "config", "user.email", "test@codespace.dev");
            await Git(dir, "config", "user.name", "Test");
            await Git(dir, "config", "commit.gpgsign", "false");
        }

        private static async Task<string> Git(string workdir, params string[] args)
        {
            var result = await new LocalProcessRunner().RunAsync(new SandboxSpec { Command = "git", Args = args, WorkingDirectory = workdir, TimeoutSeconds = 60 }, CancellationToken.None);
            if (result.Status != SandboxStatus.Success) throw new InvalidOperationException($"git {string.Join(' ', args)} failed (exit {result.ExitCode}): {result.Stderr}");
            return result.Stdout;
        }

        public void Dispose()
        {
            try { Directory.Delete(_root, recursive: true); } catch { /* best-effort */ }
        }
    }
}
