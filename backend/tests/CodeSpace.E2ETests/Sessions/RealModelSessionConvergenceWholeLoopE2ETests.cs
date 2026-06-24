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
using CodeSpace.IntegrationTests.Workflows.Supervisor;
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
/// 🟢 THE real-model SESSION CONVERGENCE arm — the deepest 真智能體 proof and the live-model counterpart of the
/// always-on <see cref="SessionConvergenceWholeLoopE2ETests"/>: a LIVE model, driving the real claude-code CLI through
/// two real session turns, must build feature B on turn 1's ACTUAL produced code without breaking feature A — proven by
/// an objective goal-relevance oracle, not a stub.
/// <list type="number">
///   <item>Turn 1 (a real launch) tasks the live coding agent to fix <c>add.sh</c> so <c>sh add.sh 7 5</c> prints 12; the
///         executor captures its real edit + pushes a real produced branch.</item>
///   <item>Turn 2 (a CONTINUE) resolves its clone to turn 1's PRODUCED branch (asserted) and tasks the agent to fix
///         <c>mul.sh</c> so <c>sh mul.sh 6 7</c> prints 42 while keeping <c>add.sh</c> working.</item>
///   <item>The real <see cref="ISupervisorAcceptanceGrader"/> re-clones turn 2's head and a BOTH-features
///         <c>check.sh</c> confirms <c>add.sh</c> (fixed on turn 1) AND <c>mul.sh</c> (fixed on turn 2) both compute
///         correctly — so add survived solely because the live model carried turn 1's branch forward. Real convergence.</item>
/// </list>
///
/// <para>REPORT-ONLY (<see cref="RealModelGate.AssessLiveAsync(string, System.Func{System.Threading.Tasks.Task{System.ValueTuple{bool, string}}}, bool)"/>
/// with <c>gating:false</c>): the SUBSTRATE convergence is already GATED by the deterministic
/// <see cref="SessionConvergenceWholeLoopE2ETests"/>; this arm OBSERVES whether a live model converges and reports ✅/⚠️
/// to the job summary. A gateway-transport outage is a non-gating infra skip; a model that fails to converge is a
/// reported ⚠️ (never a red) — the honest first-rollout tier, exactly like the real-coding arm in
/// <c>RealModelSupervisorWholeLoopE2ETests</c>. Flip to a gating best-of-N once the live wiring + a solve are confirmed.</para>
///
/// <para>Self-skips (NOT a pass) when <c>CODESPACE_LLM_*</c> are absent (fork/local), on Windows, when git is absent, or
/// when the real <c>claude</c> CLI is not installed. Routed to the real-model whole-loop CI lane (Postgres + the claude
/// CLI install + unconfined sandbox + secrets) by the <c>RealModelSession</c> name token.</para>
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "RealModel")]
[Trait("Surface", "Engine")]
public sealed class RealModelSessionConvergenceWholeLoopE2ETests : IDisposable
{
    private const string Provider = "Anthropic";   // the blessed brain wire

    private const string Turn1Goal = "Edit the file add.sh so that running `sh add.sh A B` prints the SUM of the two integer arguments A and B (e.g. `sh add.sh 7 5` prints 12). Keep it a POSIX /bin/sh script. Do not change any other file.";
    private const string Turn2Goal = "Now edit the file mul.sh so that running `sh mul.sh A B` prints the PRODUCT of the two integer arguments A and B (e.g. `sh mul.sh 6 7` prints 42). Keep it a POSIX /bin/sh script. Leave add.sh exactly as it is — do NOT modify it.";

    // The both-features goal-relevance oracle, seeded as check.sh on the base branch and carried to every produced
    // branch: exit 0 iff turn 1's add.sh (sum) AND turn 2's mul.sh (product) both compute correctly on the head.
    private const string BothFeaturesCheckSh = "#!/bin/sh\n[ \"$(sh add.sh 7 5)\" = \"12\" ] && [ \"$(sh mul.sh 6 7)\" = \"42\" ]\n";
    private const string WrongStub = "#!/bin/sh\necho 0\n";   // the agent must FIX these; absent a fix the oracle fails

    private readonly PostgresFixture _fixture;
    private readonly string? _pushBefore;

    public RealModelSessionConvergenceWholeLoopE2ETests(PostgresFixture fixture)
    {
        _fixture = fixture;
        // A task-launch single-agent run pushes its produced branch only when the deployment push flag is on (the launch
        // projection omits a per-run pushBranch). Turn 2 can only build on turn 1's branch if turn 1 actually PUSHED it.
        _pushBefore = Environment.GetEnvironmentVariable(AgentRunExecutor.PushEnabledEnvVar);
        Environment.SetEnvironmentVariable(AgentRunExecutor.PushEnabledEnvVar, "1");
    }

    public void Dispose() => Environment.SetEnvironmentVariable(AgentRunExecutor.PushEnabledEnvVar, _pushBefore);

    [Fact]
    public async Task A_live_model_continues_a_thread_and_builds_feature_B_on_turn1s_real_code()
    {
        var baseUrl = Env(RealModelSupervisorDecisionFlowTests.BaseUrlEnvVar);
        var apiKey = Env(RealModelSupervisorDecisionFlowTests.ApiKeyEnvVar);
        var model = Env(RealModelSupervisorDecisionFlowTests.ModelIdEnvVar);

        var present = new[] { baseUrl, apiKey, model }.Count(v => v is not null);
        if (present == 0) { RealModelGate.ReportSkipped(Provider, "CODESPACE_LLM_* absent (fork/local — no live model)"); return; }   // skip ≠ pass
        present.ShouldBe(3, "CODESPACE_LLM_* is partially configured — set all three or none; a partial config would self-skip this lane green proving nothing.");

        if (OperatingSystem.IsWindows()) return;
        if (!await GitReadyAsync()) return;
        if (!await ClaudeReadyAsync()) { RealModelGate.ReportSkipped(Provider, "the `claude` coding-agent CLI is not installed — the real-coding arm needs a harness binary (skip ≠ pass)"); return; }

        var jobClient = ResolveJobClient();
        jobClient.Clear();
        jobClient.AutoExecute = true;

        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);

        using var remote = new BareRemote();
        await remote.SeedBaseAsync(new() { ["check.sh"] = BothFeaturesCheckSh, ["add.sh"] = WrongStub, ["mul.sh"] = WrongStub });
        var repoId = await SeedBoundRepositoryAsync(teamId, remote.Url, "main");

        var credId = await SeedAgentModelAsync(teamId, BaseUrlFor(baseUrl!), apiKey!, model!);

        // REPORT-ONLY: ✅ = the live model converged (built B on turn 1's real branch, both features pass); ⚠️ = it did
        // not (reported, never gating); a gateway-transport outage is a non-gating infra skip. No assertion inside drive,
        // so only a genuine WIRING exception propagates (which is what we WANT surfaced while bringing the arm up).
        await RealModelGate.AssessLiveAsync(Provider, async () =>
        {
            // ── TURN 1: the live agent fixes add.sh; the executor pushes a real produced branch. ──
            var first = await LaunchAsync(LaunchRequest(teamId, userId, repoId, credId, model!, Turn1Goal, continueSessionId: null));
            await ExecuteAsync(first.RunId, jobClient);
            var firstRun = await LoadRunAsync(first.RunId);
            var turn1Branch = ReadProducedBranch(firstRun);

            if (turn1Branch is null)
                return (false, $"turn 1 produced no branch — the live agent did not author+push a fix (status={firstRun.Status}). add.sh fix not carried.");

            // ── TURN 2: a CONTINUE — must clone turn 1's branch and add the mul.sh fix without breaking add.sh. ──
            var second = await LaunchAsync(LaunchRequest(teamId, userId, repoId, credId, model!, Turn2Goal, continueSessionId: first.SessionId));
            var baseRef = await ReadAgentBaseRefAsync(second.RunId);
            await ExecuteAsync(second.RunId, jobClient);
            var secondRun = await LoadRunAsync(second.RunId);
            var turn2Branch = ReadProducedBranch(secondRun);

            if (turn2Branch is null)
                return (false, $"turn 2 produced no branch (status={secondRun.Status}); baseRef carried={baseRef == turn1Branch} (baseRef={baseRef}, turn1={turn1Branch}).");

            // ── OBJECTIVE CONVERGENCE: add.sh (fixed turn 1) AND mul.sh (fixed turn 2) both pass on turn 2's head. ──
            var grade = await GradeAsync(repoId, teamId, turn2Branch);
            var carried = baseRef == turn1Branch;
            var converged = carried && grade.Passed;

            return (converged,
                $"{Provider} '{model}' session convergence: baseRef-carried={carried} (turn1={turn1Branch}, turn2-baseRef={baseRef}), both-features-grade={grade.Passed} ({grade.Detail}). "
              + (converged ? "DROVE — the live model built feature B on turn 1's real code." : "did NOT converge (reported, not gating)."));
        }, gating: false);
    }

    // ── Flow helpers ───────────────────────────────────────────────────────────────────────────────────────────────

    private static TaskLaunchRequest LaunchRequest(Guid teamId, Guid userId, Guid repoId, Guid modelCredentialId, string model, string goal, Guid? continueSessionId) => new()
    {
        TeamId = teamId, ActorUserId = userId, SurfaceKind = TaskLaunchSurfaceKinds.Chat,
        TaskText = goal, RepositoryId = repoId, ContinueSessionId = continueSessionId,
        RequestedEffort = TaskEffortModes.Quick, Autonomy = "Trusted",   // a real coding agent needs egress (gateway) + file write
        Overrides = new TaskExecutionOverrides { Harness = "claude-code", RunnerKind = "local", ModelCredentialId = modelCredentialId, Model = model },
    };

    private async Task ExecuteAsync(Guid runId, InMemoryBackgroundJobClient jobClient)
    {
        await RunEngineAsync(runId);
        await jobClient.WaitForPendingAsync();
    }

    private async Task<BenchmarkGrade> GradeAsync(Guid repoId, Guid teamId, string branch)
    {
        using var scope = _fixture.BeginScope();
        return await scope.Resolve<ISupervisorAcceptanceGrader>().GradeAsync(repoId, teamId, branch, new[] { "sh", "check.sh" }, 120, CancellationToken.None);
    }

    private static string? ReadProducedBranch(WorkflowRun run)
    {
        if (string.IsNullOrWhiteSpace(run.OutputsJson)) return null;
        var root = JsonDocument.Parse(run.OutputsJson!).RootElement;
        return root.TryGetProperty("branch", out var b) && b.ValueKind == JsonValueKind.String ? b.GetString() : null;
    }

    // ── DI / engine plumbing ─────────────────────────────────────────────────────────────────────────────────────────

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

    private async Task<string?> ReadAgentBaseRefAsync(Guid runId)
    {
        var run = await LoadRunAsync(runId);
        var root = JsonDocument.Parse(run.DefinitionSnapshotJson!).RootElement;
        var agent = root.GetProperty("nodes").EnumerateArray().Single(n => n.GetProperty("id").GetString() == "agent");
        return agent.GetProperty("inputs").TryGetProperty("baseRef", out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    }

    private static string? Env(string name) => string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(name)) ? null : Environment.GetEnvironmentVariable(name);

    /// <summary>Anthropic's client appends <c>/v1/messages</c> to the host base — pass the gateway host as-is.</summary>
    private static string BaseUrlFor(string baseUrl) => baseUrl.TrimEnd('/');

    private static async Task<bool> GitReadyAsync()
    {
        if (OperatingSystem.IsWindows()) return false;
        try { return (await new LocalProcessRunner().RunAsync(new SandboxSpec { Command = "git", Args = new[] { "--version" }, TimeoutSeconds = 10 }, CancellationToken.None)).Status == SandboxStatus.Success; }
        catch { return false; }
    }

    /// <summary>Whether the real <c>claude</c> coding-agent CLI is on PATH — the live-coding arm self-skips (NOT a pass) when it is absent.</summary>
    private static async Task<bool> ClaudeReadyAsync()
    {
        if (OperatingSystem.IsWindows()) return false;
        try { return (await new LocalProcessRunner().RunAsync(new SandboxSpec { Command = "claude", Args = new[] { "--version" }, TimeoutSeconds = 15 }, CancellationToken.None)).Status == SandboxStatus.Success; }
        catch { return false; }
    }

    /// <summary>Seed a credentialed model row the launched agent authenticates with (the claude harness decrypts its key + projects ANTHROPIC_BASE_URL/AUTH_TOKEN). Returns the ModelCredential id → the launch's <c>ModelCredentialId</c> override.</summary>
    private async Task<Guid> SeedAgentModelAsync(Guid teamId, string baseUrl, string apiKey, string modelId)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var encryptor = scope.Resolve<CodeSpace.Core.Services.Credentials.IPayloadEncryptor>();

        var credId = Guid.NewGuid();
        db.ModelCredential.Add(new ModelCredential
        {
            Id = credId, TeamId = teamId, Provider = Provider, DisplayName = "live agent cred",
            EncryptedApiKey = encryptor.Encrypt(apiKey), BaseUrl = baseUrl, Status = CredentialStatus.Active,
            CreatedBy = SystemUsers.SeederId, LastModifiedBy = SystemUsers.SeederId,
        });
        db.ModelCredentialModel.Add(new ModelCredentialModel { Id = Guid.NewGuid(), ModelCredentialId = credId, ModelId = modelId, Source = ModelSource.Manual, SupportsStructuredOutput = true, Enabled = true });

        await db.SaveChangesAsync();
        return credId;
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

    /// <summary>A bare local repo standing in for the remote. GUID-suffixed; best-effort cleaned. Mirrors SupervisorWholeLoopE2ETests.BareRemote.</summary>
    private sealed class BareRemote : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "cs-rm-session-converge-" + Guid.NewGuid().ToString("N"));
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
}
