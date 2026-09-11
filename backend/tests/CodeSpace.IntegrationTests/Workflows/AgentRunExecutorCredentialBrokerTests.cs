using System.Net;
using System.Net.Http;
using System.Text.Json;
using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Agents.Credentials;
using CodeSpace.Core.Services.Agents.Credentials.Broker;
using CodeSpace.Core.Services.Agents.Sandbox;
using CodeSpace.Core.Services.Agents.Sandbox.Runners;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Enums;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace CodeSpace.IntegrationTests.Workflows;

/// <summary>
/// The brokered model credential, driven through the REAL executor / <c>AgentRunService</c> against real Postgres and
/// the real <see cref="LocalProcessRunner"/> — so the environment asserted is the one a real <c>/bin/sh</c> child
/// actually saw, and the refusal asserted is a real HTTP answer from the real broker.
///
/// <para>What each test is really guarding is an ORDER or an ABSENCE, neither of which a unit test can observe: that
/// the provider key is absent from a live child's environment, that a cancel's revocation lands BEFORE its kill, and
/// that a run's heartbeat keeps the lease alive across a window in which it would otherwise have lapsed.</para>
/// </summary>
public partial class AgentRunExecutorTests
{
    private const string BrokeredProvider = "Anthropic";

    [Fact]
    public async Task A_brokered_runs_child_holds_a_broker_address_and_a_run_token_and_no_provider_key()
    {
        if (OperatingSystem.IsWindows()) return;

        const string plaintextKey = "sk-brokered-run-must-never-see-this";

        var teamId = await SeedTeamAsync();
        var credId = await SeedModelCredentialAsync(teamId, BrokeredProvider, plaintextKey);
        var runId = await CreateRunWithCredentialAsync(teamId, credId);

        using var broker = new LoopbackModelCredentialBroker();
        var harness = new BrokerableScriptedHarness(BrokeredProvider,
            "if [ -n \"$SCRIPTED_MODEL_KEY\" ]; then echo KEY_PRESENT; else echo KEY_ABSENT; fi; " +
            "if [ -n \"$SCRIPTED_BASE_URL\" ]; then echo BASE_PRESENT; else echo BASE_ABSENT; fi; " +
            "if [ -n \"$SCRIPTED_RUN_TOKEN\" ]; then echo TOKEN_PRESENT; else echo TOKEN_ABSENT; fi");

        await ExecuteAsync(runId, harness, credentialBroker: broker);

        using var scope = _fixture.BeginScope();
        var svc = scope.Resolve<IAgentRunService>();
        var run = await svc.GetAsync(runId, CancellationToken.None);

        run.Status.ShouldBe(AgentRunStatus.Succeeded);

        var lines = (await svc.GetEventsAsync(runId, teamId, 0, CancellationToken.None)).Select(e => e.Text).ToList();

        lines.ShouldContain("KEY_ABSENT", "the provider key must not be in the child's environment — that is the whole claim of this slice");
        lines.ShouldContain("BASE_PRESENT", "the child must be pointed at the broker, or it has no endpoint to call at all");
        lines.ShouldContain("TOKEN_PRESENT", "the child must carry its per-run bearer");

        // The env the harness was ACTUALLY handed — the key must not be hiding in another variable the script did not
        // happen to name, and the base URL must still be a token for the runner to resolve at launch.
        var injected = harness.BuiltTask!.Environment;
        injected.Values.ShouldNotContain(plaintextKey, "no injected variable may carry the upstream key under brokerage");
        injected["SCRIPTED_BASE_URL"].ShouldContain(SandboxSpec.ModelBrokerHostToken);
        injected["SCRIPTED_RUN_TOKEN"].Length.ShouldBeGreaterThan(32);

        run.ResultJson!.ShouldNotContain(plaintextKey);
        run.Error?.ShouldNotContain(plaintextKey);
    }

    [Fact]
    public async Task A_brokered_run_records_that_it_was_brokered_and_says_nothing_about_a_direct_injection()
    {
        if (OperatingSystem.IsWindows()) return;

        var teamId = await SeedTeamAsync();
        var credId = await SeedModelCredentialAsync(teamId, BrokeredProvider, "sk-posture-fixture");
        var runId = await CreateRunWithCredentialAsync(teamId, credId);

        using var broker = new LoopbackModelCredentialBroker();

        await ExecuteAsync(runId, new BrokerableScriptedHarness(BrokeredProvider, "echo done"), credentialBroker: broker);

        using var scope = _fixture.BeginScope();
        var stored = await scope.Resolve<CodeSpaceDbContext>().AgentRun.AsNoTracking().Where(r => r.Id == runId).Select(r => r.SandboxConfinementJson).SingleAsync();

        var confinement = JsonSerializer.Deserialize<SandboxConfinement>(stored!, AgentJson.Options).ShouldNotBeNull();

        confinement.ModelCredentialBrokered.ShouldBe(true,
            customMessage: "the launch has to record which runs were handed the key — it is the only thing a reader can consult afterwards, and the posture sentence keys off it");
        AgentAutonomyPolicy.DescribeNetwork(AgentAutonomyLevel.Trusted, AgentAutonomyLevel.Trusted, AgentAutonomyLevel.Unleashed, confinement)
            .ShouldNotContain(AgentAutonomyPolicy.DirectModelCredentialCaveat, customMessage: "a brokered run must not disclose a direct injection it did not do");
    }

    [Fact]
    public async Task A_run_whose_credential_cannot_be_brokered_discloses_the_direct_injection()
    {
        if (OperatingSystem.IsWindows()) return;

        var teamId = await SeedTeamAsync();
        var credId = await SeedModelCredentialAsync(teamId, BrokeredProvider, "sk-unbrokered-fixture");
        var runId = await CreateRunWithCredentialAsync(teamId, credId);

        // No broker at all — the deployment shape that exists today. The key is injected (nothing else would work),
        // and the run's record must say so rather than leaving a reader to assume it was withdrawable.
        await ExecuteAsync(runId, new BrokerableScriptedHarness(BrokeredProvider, "echo done"));

        using var scope = _fixture.BeginScope();
        var stored = await scope.Resolve<CodeSpaceDbContext>().AgentRun.AsNoTracking().Where(r => r.Id == runId).Select(r => r.SandboxConfinementJson).SingleAsync();

        var confinement = JsonSerializer.Deserialize<SandboxConfinement>(stored!, AgentJson.Options).ShouldNotBeNull();

        confinement.ModelCredentialBrokered.ShouldBe(false);
        AgentAutonomyPolicy.DescribeNetwork(AgentAutonomyLevel.Trusted, AgentAutonomyLevel.Trusted, AgentAutonomyLevel.Unleashed, confinement)
            .ShouldEndWith(AgentAutonomyPolicy.DirectModelCredentialCaveat,
                customMessage: "an unbrokered run's posture must disclose that its credential cannot be withdrawn mid-run");
    }

    [Fact]
    public async Task A_deployment_that_requires_confinement_refuses_the_run_rather_than_inject_the_key()
    {
        if (OperatingSystem.IsWindows()) return;

        const string plaintextKey = "sk-must-not-be-injected-when-required";

        var teamId = await SeedTeamAsync();
        var credId = await SeedModelCredentialAsync(teamId, BrokeredProvider, plaintextKey);
        var runId = await CreateRunWithCredentialAsync(teamId, credId);

        var harness = new BrokerableScriptedHarness(BrokeredProvider, "echo should-not-run");

        // No broker AND confinement mandated: the fall-back the un-mandated deployment takes is exactly what must not
        // happen here. Mutation — make the fallback reachable when required — and the run lands Succeeded with the
        // key in the child's env, which is what the setting exists to forbid.
        using (CodeSpace.Core.Settings.RuntimeSettings.Override(current => current with { RequireSandboxConfinement = true }))
            await ExecuteAsync(runId, harness);

        using var scope = _fixture.BeginScope();
        var run = await scope.Resolve<IAgentRunService>().GetAsync(runId, CancellationToken.None);

        run.Status.ShouldBe(AgentRunStatus.Failed, "a deployment that mandates confinement must refuse a run whose credential cannot be brokered");
        run.Error.ShouldNotBeNull();
        run.Error!.ShouldContain("Sandbox:RequireConfinement", customMessage: "the refusal must name the setting an operator would change");
        run.Error!.ShouldNotContain(plaintextKey);

        JsonSerializer.Deserialize<AgentRunResult>(run.ResultJson!, AgentJson.Options)!.ExitReason
            .ShouldBe(CodeSpace.Messages.Failures.FailureCodes.ModelCredentialBrokerUnavailable,
                customMessage: "the refusal has to land under its OWN failure code: 'executor-error' tells an operator nothing about which wall the run hit, and no reader can tell it apart from a harness that crashed");

        (harness.BuiltTask?.Environment.Values ?? Array.Empty<string>()).ShouldNotContain(plaintextKey,
            "the refusal must land BEFORE the projection — a run that failed after the key was already put in a spec is not fail-closed");
    }

    [Fact]
    public async Task A_cancel_refuses_the_credential_before_the_kill_is_issued()
    {
        var teamId = await SeedTeamAsync();
        var runId = await SeedBrokerCancelRunAsync(teamId);

        using var broker = LoopbackModelCredentialBroker.ForTest(new AlwaysOkUpstream());
        var brokered = await broker.OpenAsync(
            new() { RunId = runId, TeamId = teamId, Epoch = 1, Upstream = new() { Provider = BrokeredProvider, ApiKey = "sk-cancel-fixture-key" }, Ttl = TimeSpan.FromMinutes(5) },
            CancellationToken.None);

        if (brokered is null) return;   // this host cannot bind a listener — nothing to order

        (await ProxiedCallAsync(brokered)).ShouldBe(HttpStatusCode.OK, "precondition: the lease answers before the cancel");

        // The kill BLOCKS at entry, so the window between "revoke" and "the process is actually gone" is open for as
        // long as the test needs. A refusal observed inside it can only mean the revocation ran FIRST — which is the
        // ordering the cancel path promises, and the one that decides whether a cancelled agent gets one more turn on
        // the tenant's key. Move the revoke after the terminate and this observes 200.
        var kill = new BlockingTerminateRunner();
        using var scope = _fixture.BeginScope();
        var service = BuildCancelService(scope, kill, broker);

        var cancel = service.CancelRunningAsync(runId, "operator cancel", AgentRunAbandonCause.OperatorCancelled, CancellationToken.None);

        (await kill.Entered.Task.WaitAsync(TimeSpan.FromSeconds(15))).ShouldBeTrue("the cancel never reached TerminateAsync within 15s — check CancelRunningAsync's CAS; the run must be Running at epoch 1");

        (await ProxiedCallAsync(brokered)).ShouldBe(HttpStatusCode.Unauthorized,
            "the credential must already be refused while the kill is still in flight — a signal races the agent's next model call, a withdrawn lease does not");

        kill.Release.SetResult();
        (await cancel).ShouldBeTrue();
    }

    [Fact]
    public async Task The_reconcilers_abandon_refuses_the_credential_before_the_kill_is_issued()
    {
        var teamId = await SeedTeamAsync();
        var runId = Guid.NewGuid();

        using var broker = LoopbackModelCredentialBroker.ForTest(new AlwaysOkUpstream());
        var brokered = await broker.OpenAsync(
            new() { RunId = runId, TeamId = teamId, Epoch = 1, Upstream = new() { Provider = BrokeredProvider, ApiKey = "sk-abandon-fixture-key" }, Ttl = TimeSpan.FromMinutes(5) },
            CancellationToken.None);

        if (brokered is null) return;   // this host cannot bind a listener — nothing to order

        var spool = Path.Combine(Path.GetTempPath(), "cs-broker-abandon-" + runId.ToString("N"));
        await SeedStaleBrokeredRunAsync(teamId, runId, spool);

        (await ProxiedCallAsync(brokered)).ShouldBe(HttpStatusCode.OK, "precondition: the lease answers before the sweep");

        // The abandon's kill BLOCKS at entry, holding the window open — but only for THIS run's handle, so the
        // deployment-wide sweep is not wedged by another suite's stale row on its way here. The probe throws, which
        // is the ladder branch that kills a maybe-alive orphan and abandons it. A refusal observed inside that window
        // can only mean the revocation ran FIRST: the same ordering the cancel path promises, for the same reason.
        // Move the revoke after the terminate and this observes 200.
        var kill = new BlockingTerminateRunner { OnlyForSpool = spool, ProbeThrows = true };
        using var scope = _fixture.BeginScope();
        var sweep = Task.Run(() => BuildReconciler(scope, kill, broker).ReconcileAsync(CancellationToken.None));

        (await kill.Entered.Task.WaitAsync(TimeSpan.FromSeconds(30))).ShouldBeTrue(
            "the sweep never reached TerminateAsync for this run within 30s — check that the seeded row is still Running with an expired lease and no recent events, and that its handle kind matches the substituted runner");

        (await ProxiedCallAsync(brokered)).ShouldBe(HttpStatusCode.Unauthorized,
            "an abandoned orphan's credential must already be refused while its kill is still in flight — a signal races the agent's next model call, a withdrawn lease does not");

        kill.Release.SetResult();
        await sweep;
    }

    [Fact]
    public async Task A_heartbeat_keeps_the_lease_alive_past_one_ttl_and_completion_withdraws_it()
    {
        if (OperatingSystem.IsWindows()) return;

        // Floor the liveness window so the heartbeat fires every 5s (its own minimum) and the lease TTL is 10s —
        // short enough to observe a renewal in a test, and the shortest the production contract allows.
        var priorWindow = Environment.GetEnvironmentVariable(AgentRunLiveness.WindowEnvVar);
        Environment.SetEnvironmentVariable(AgentRunLiveness.WindowEnvVar, "00:00:15");
        try
        {
            AgentRunLiveness.HeartbeatInterval.ShouldBe(TimeSpan.FromSeconds(5));
            var ttl = ModelCredentialLease.Ttl;

            var teamId = await SeedTeamAsync();
            var credId = await SeedModelCredentialAsync(teamId, BrokeredProvider, "sk-heartbeat-fixture");
            var runId = await CreateRunWithCredentialAsync(teamId, credId);

            using var release = new TempDir();
            var releaseFile = Path.Combine(release.Path, "release");
            using var broker = new LoopbackModelCredentialBroker();

            var harness = new BrokerableScriptedHarness(BrokeredProvider, $"while [ ! -f '{releaseFile}' ]; do sleep 0.2; done; echo done");
            var run = ExecuteAsync(runId, harness, credentialBroker: broker);

            await WaitUntilAsync(() => broker.HasLease(runId), TimeSpan.FromSeconds(20), "the run never opened a credential lease");

            // Past the ORIGINAL expiry: still live can only mean the run's heartbeat renewed it.
            await Task.Delay(ttl + TimeSpan.FromSeconds(3));

            broker.HasLease(runId).ShouldBeTrue(
                $"the lease lapsed after {ttl.TotalSeconds}s while the run was still working — the heartbeat is not renewing it, so every run longer than one TTL 401s mid-turn (check AgentRunExecutor's heartbeat wiring)");

            await File.WriteAllTextAsync(releaseFile, "go");
            await run;

            broker.HasLease(runId).ShouldBeFalse("a finished run's credential must be withdrawn at once, not left to lapse");
        }
        finally { Environment.SetEnvironmentVariable(AgentRunLiveness.WindowEnvVar, priorWindow); }
    }

    // ── Fixtures ──────────────────────────────────────────────────────────────────────────────────────────────────

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan budget, string failure)
    {
        var deadline = DateTimeOffset.UtcNow + budget;

        while (DateTimeOffset.UtcNow < deadline)
        {
            if (condition()) return;
            await Task.Delay(100);
        }

        throw new Xunit.Sdk.XunitException($"{failure} (waited {budget.TotalSeconds}s)");
    }

    private static async Task<HttpStatusCode> ProxiedCallAsync(BrokeredModelCredential brokered)
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        using var request = new HttpRequestMessage(HttpMethod.Post, brokered.BaseUrl.Replace(SandboxSpec.ModelBrokerHostToken, "127.0.0.1", StringComparison.Ordinal) + "/v1/messages")
        {
            Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json"),
        };
        request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {brokered.RunToken}");

        return (await client.SendAsync(request)).StatusCode;
    }

    /// <summary>A Running run at epoch 1 carrying a durable handle, so <c>CancelRunningAsync</c> resolves a runner to kill. No real process: the kill is what the test intercepts.</summary>
    private async Task<Guid> SeedBrokerCancelRunAsync(Guid teamId)
    {
        var runId = Guid.NewGuid();
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        var handle = new SandboxHandle { Kind = SandboxKinds.Local, ProcessId = Environment.ProcessId, SpoolDirectory = Path.GetTempPath(), Deadline = DateTimeOffset.UtcNow.AddMinutes(5) };

        db.AgentRun.Add(new AgentRun { Id = runId, TeamId = teamId, Harness = "scripted", Status = AgentRunStatus.Running, FenceEpoch = 1, RunnerHandleJson = JsonSerializer.Serialize(handle, AgentJson.Options) });
        await db.SaveChangesAsync();

        return runId;
    }

    /// <summary>
    /// A stale Running run whose durable handle routes to the test's runner. Its lease expired YEARS ago so the row
    /// sorts first in the reconciler's batch — the sweep is deployment-wide and bounded
    /// (<see cref="AgentRunReconcilerService.BatchSize"/>), so a row that sorts late can fall out of it entirely on a
    /// shared test database.
    /// </summary>
    private async Task SeedStaleBrokeredRunAsync(Guid teamId, Guid runId, string spoolDirectory)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        var handle = new SandboxHandle { Kind = SandboxKinds.Local, ProcessId = Environment.ProcessId, SpoolDirectory = spoolDirectory, Deadline = DateTimeOffset.UtcNow.AddMinutes(30), ModelBrokerRunToken = "brokered-run-token-fixture" };

        db.AgentRun.Add(new AgentRun
        {
            Id = runId, TeamId = teamId, Harness = "scripted", Status = AgentRunStatus.Running, FenceEpoch = 1,
            StartedAt = DateTimeOffset.UtcNow.AddHours(-1), HeartbeatAt = DateTimeOffset.UtcNow.AddHours(-1), LeaseExpiresAt = DateTimeOffset.UtcNow.AddYears(-5),
            RunnerHandleJson = JsonSerializer.Serialize(handle, AgentJson.Options),
        });
        await db.SaveChangesAsync();
    }

    /// <summary>The REAL reconciler with the same two substitutions <see cref="BuildCancelService"/> makes — the runner registry (so the kill is the interceptable one) and the broker (so the lease under test is the one the test holds).</summary>
    private static IAgentRunReconcilerService BuildReconciler(ILifetimeScope scope, ISandboxRunner runner, IModelCredentialBroker broker) =>
        new AgentRunReconcilerService(
            scope.Resolve<CodeSpaceDbContext>(),
            scope.Resolve<IAgentRunService>(),
            scope.Resolve<IAgentRunCompletionNotifier>(),
            scope.Resolve<CodeSpace.Core.Services.Jobs.ICodeSpaceBackgroundJobClient>(),
            new SandboxRunnerRegistry(new[] { runner }),
            scope.Resolve<CodeSpace.Core.Services.Agents.Mcp.IToolCallLedgerService>(),
            scope.Resolve<CodeSpace.Core.Services.Agents.Capture.ICaptureIntentService>(),
            scope.Resolve<CodeSpace.Core.Services.Agents.Capture.INativeRecordPlane>(),
            scope.Resolve<CodeSpace.Core.Services.Agents.Recovery.IRunCleanupLedger>(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<AgentRunReconcilerService>.Instance,
            credentialBroker: broker);

    /// <summary>
    /// The REAL <see cref="AgentRunService"/> with two substitutions: the runner registry (so the kill is the
    /// interceptable one) and the broker (so the lease under test is the one the test holds). Everything else — the
    /// DbContext, the authority service, the record plane — is the production object from the scope.
    /// </summary>
    private static IAgentRunService BuildCancelService(Autofac.ILifetimeScope scope, ISandboxRunner runner, IModelCredentialBroker broker) =>
        new AgentRunService(
            scope.Resolve<CodeSpaceDbContext>(),
            new AgentRunRuntimeServices(scope.Resolve<IAdmissionController>(), new SandboxRunnerRegistry(new[] { runner }), scope.Resolve<AgentRunDurabilityServices>(), scope.Resolve<CodeSpace.Core.Services.Learning.IAgentLessonInjector>()),
            scope.Resolve<CodeSpace.Core.Services.Agents.Authority.ExecutionAuthorityService>(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<AgentRunService>.Instance,
            credentialBroker: broker);

    /// <summary>A durable runner whose TerminateAsync BLOCKS until released — the window in which the ordering of revoke-then-kill is observable.</summary>
    private sealed class BlockingTerminateRunner : ISandboxRunner, ISandboxDurableRunner
    {
        public TaskCompletionSource<bool> Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Block (and throw from the probe) only for the handle at this spool. Null blocks every handle, which is right for a cancel — it names one run — and wrong for the reconciler's deployment-wide sweep, where another suite's stale row would wedge the pass before it reached this test's.</summary>
        public string? OnlyForSpool { get; init; }

        /// <summary>Make ProbeAsync throw, the ladder branch that kills a maybe-alive orphan and abandons it. Default: a live process, which is the re-attach branch.</summary>
        public bool ProbeThrows { get; init; }

        public string Kind => SandboxKinds.Local;

        public async Task TerminateAsync(SandboxHandle handle, CancellationToken cancellationToken)
        {
            if (!IsMine(handle)) return;

            Entered.TrySetResult(true);
            await Release.Task;
        }

        public Task<SandboxResult> RunAsync(SandboxSpec spec, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<SandboxHandle> LaunchAsync(SandboxSpec spec, string spoolKey, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<SandboxResult> AttachAsync(SandboxHandle handle, Func<SandboxOutputFrame, CancellationToken, Task> onStdoutFrame, CancellationToken cancellationToken, Func<long, CancellationToken, Task>? onCheckpoint = null) => throw new NotSupportedException();

        public Task<SandboxProbe> ProbeAsync(SandboxHandle handle, CancellationToken cancellationToken) =>
            ProbeThrows && IsMine(handle)
                ? Task.FromException<SandboxProbe>(new IOException("the probe cannot be answered from this host (test fixture)"))
                : Task.FromResult(new SandboxProbe { State = SandboxRunState.Running });

        private bool IsMine(SandboxHandle handle) => OnlyForSpool is not { } mine || handle.SpoolDirectory == mine;
    }

    /// <summary>The provider, answering 200 to anything — the test asserts WHETHER a call is relayed, never what a model said.</summary>
    private sealed class AlwaysOkUpstream : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"ok\":true}") });
    }

    /// <summary>
    /// A scripted harness that can be brokered: it projects a direct credential (like the real ones) AND a brokered
    /// one, and it carries <c>task.Environment</c> into its spec so whatever was projected actually reaches the child.
    /// Its variable names are deliberately its own — the assertion is about which KIND of value lands, not about
    /// Anthropic's or OpenAI's spellings, which their own harness pin tests own.
    /// </summary>
    private sealed class BrokerableScriptedHarness(string provider, string script) : IAgentHarness, IModelCredentialProjector, IBrokeredModelCredentialProjector
    {
        public const string KeyEnvVar = "SCRIPTED_MODEL_KEY";
        public const string BaseUrlEnvVar = "SCRIPTED_BASE_URL";
        public const string RunTokenEnvVar = "SCRIPTED_RUN_TOKEN";

        public string Kind => "scripted-projector";
        public string Version => "test";
        public IReadOnlyList<string> Models { get; } = new[] { "test-model" };

        public AgentTask? BuiltTask { get; private set; }

        public SandboxSpec BuildInvocation(AgentTask task)
        {
            BuiltTask = task;
            return new SandboxSpec { Command = "/bin/sh", Args = new[] { "-c", script }, WorkingDirectory = task.WorkspaceDirectory, Environment = task.Environment, TimeoutSeconds = task.TimeoutSeconds };
        }

        public IReadOnlyList<AgentEvent> ParseEvents(string rawLine) =>
            string.IsNullOrWhiteSpace(rawLine) ? Array.Empty<AgentEvent>() : new[] { new AgentEvent { Kind = AgentEventKind.AssistantMessage, Text = rawLine.Trim() } };

        public IAgentEventFolder CreateFolder() => new TestEventFolder((fold, exitCode) =>
            exitCode == 0
                ? new AgentRunResult { Status = AgentRunStatus.Succeeded, ExitReason = "completed", Summary = fold.LastText }
                : new AgentRunResult { Status = AgentRunStatus.Failed, ExitReason = "non-zero-exit", Error = $"exit {exitCode}" });

        public IReadOnlyList<string> SupportedProviders { get; } = new[] { provider };

        public IReadOnlyDictionary<string, string> ProjectToEnv(ResolvedModelCredential credential) =>
            new Dictionary<string, string> { [KeyEnvVar] = credential.ApiKey ?? "" };

        public IReadOnlyDictionary<string, string> ProjectBrokered(BrokeredModelCredential brokered) =>
            new Dictionary<string, string> { [BaseUrlEnvVar] = brokered.BaseUrl, [RunTokenEnvVar] = brokered.RunToken };
    }
}
