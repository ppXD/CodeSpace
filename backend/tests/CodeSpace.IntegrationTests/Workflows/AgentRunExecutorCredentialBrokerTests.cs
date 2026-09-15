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
using CodeSpace.Core.Services.Agents.AgentRunLogging;
using CodeSpace.Core.Services.Agents.Sandbox.Runners;
using CodeSpace.Core.Services.Credentials;
using CodeSpace.Core.Services.Workflows.Artifacts.Providers.Local;
using CodeSpace.Messages.Dtos.Agents;
using CodeSpace.IntegrationTests.Workflows.Infrastructure;
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

    [Fact]
    public async Task A_worker_shutting_down_ends_every_brokered_run_it_owns_typed_rather_than_leaving_them_running()
    {
        if (OperatingSystem.IsWindows()) return;

        var teamId = await SeedTeamAsync();
        var credId = await SeedModelCredentialAsync(teamId, BrokeredProvider, "sk-shutdown-drain-fixture");
        var runIds = new[] { await CreateRunWithCredentialAsync(teamId, credId), await CreateRunWithCredentialAsync(teamId, credId) };

        using var broker = new LoopbackModelCredentialBroker();
        using var shutdown = new CancellationTokenSource();

        // Two agents this worker brokered, both still working when the pod takes its SIGTERM. Their leases live in
        // THIS process's memory behind THIS process's listener, so neither survives the restart — and each child
        // sleeps far past its own deadline, so anything that left them Running would be a run degrading in silence.
        var lifetime = new FakeHostLifetime();
        // PRODUCTION SHAPE: a real INativeRecordPlane, because the drain's fold re-opens a resumed capture at the
        // SAME worker fence epoch as this pass's still-open one, and with a null plane that second open is a no-op.
        //
        // NO log-capture bridge, and that is ALSO the production shape: AgentRunLogCaptureBridge carries no
        // IDependency marker and is registered nowhere in backend/src, so a deployed executor's _logCapture is null
        // and it always takes its PassthroughLogCaptureSession branch. An earlier revision hand-built the bridge to
        // assert something about capture streams; that asserted a property of a component no deployment runs, and
        // building it also exposed a pre-existing hang that killed a CI host. Both are in the PR body.
        var executions = runIds.Select(runId => ExecuteUntilShutdownAsync(runId, broker, shutdown.Token, lifetime, productionCapturePlanes: true)).ToArray();

        await WaitUntilAsync(() => runIds.All(broker.HasLease), TimeSpan.FromSeconds(30), "the runs never opened their credential leases");
        await WaitUntilAsync(() => runIds.All(id => HandleOf(id) is not null), TimeSpan.FromSeconds(30), "the runs never persisted a durable handle, so there was no launched agent for a shutdown to account for");

        var pids = runIds.Select(id => HandleOf(id)!.ProcessId).ToArray();
        pids.ShouldAllBe(pid => ProcessIsAlive(pid), "precondition: both agents are alive at the moment the worker is told to go");

        // The host announces it is stopping BEFORE the job tokens are cancelled, exactly as the generic host does.
        // That announcement — not the cancelled token — is what the tear-down arm is allowed to act on.
        lifetime.Stop();
        shutdown.Cancel();
        // BOUNDED, and the reason is a CI incident: an unbounded WhenAll here produced no output for five minutes
        // and the blame collector killed the test host, which reports as a crash rather than as whatever actually
        // went wrong. Every wait in this test names the signal it waits on (Rule 12.10).
        await AwaitWithinAsync(Task.WhenAll(executions), TimeSpan.FromSeconds(120),
            "the draining executors never returned — the landing is bounded by ShutdownLeaseLandingBudget, so a wait this long means something inside it is not honouring its token");

        foreach (var runId in runIds)
        {
            using var scope = _fixture.BeginScope();
            var run = await scope.Resolve<IAgentRunService>().GetAsync(runId, CancellationToken.None);

            run.Status.ShouldBe(AgentRunStatus.Failed,
                $"run {runId} was left Running by a worker that was taking its model access with it — the outcome has to land here, inside the drain, not at the agent's spec timeout");
            var result = JsonSerializer.Deserialize<AgentRunResult>(run.ResultJson!, AgentJson.Options).ShouldNotBeNull();

            result.ExitReason.ShouldBe(CodeSpace.Messages.Failures.FailureCodes.ModelCredentialLeaseLost);
            run.Error!.ShouldNotContain("provider", Shouldly.Case.Insensitive, "a restart of ours must never be dressed as a provider outage");

            // The drain FOLDS. A bare landing would carry no changed files, which the supervisor's post-hoc grade
            // reads as no-branch-or-repo with no work present — a real verdict failure rather than infra — so a
            // rolling restart would burn a turn's no-progress budget in seconds. And without the session id every
            // retry is cold, re-paying for work already bought.
            result.SessionId.ShouldBe(ShutdownFactSessionId,
                $"run {runId} landed without the session id its agent announced — the retry this failure tells an operator to run would start cold");
            result.TokenUsage.ShouldNotBeNull($"run {runId} landed reporting no spend, though its agent burned the tenant's tokens before the drain stopped it");
            (result.TokenUsage!.InputTokens + result.TokenUsage.OutputTokens).ShouldBeGreaterThan(0);

            var confinement = JsonSerializer.Deserialize<SandboxConfinement>(run.SandboxConfinementJson!, AgentJson.Options).ShouldNotBeNull();
            confinement.ModelCredentialLeaseLost.ShouldBeTrue("the posture is stamped under the fence BEFORE the kill, so a landing that fails still leaves the next sweep the cause");

            broker.HasLease(runId).ShouldBeFalse("the credential is withdrawn before the kill, so the agent cannot spend on the seconds it has left");

        }

        foreach (var pid in pids)
            await WaitUntilAsync(() => !ProcessIsAlive(pid), TimeSpan.FromSeconds(15), $"the agent (pid {pid}) was still alive after its run was landed lease-lost; an agent that cannot call a model must be stopped, not just recorded — diagnose with `ps -p {pid} -o pid,stat,etime,command`");
    }

    [Fact]
    public async Task A_drained_run_lands_with_the_files_its_agent_actually_changed()
    {
        if (OperatingSystem.IsWindows() || !await GitAvailableAsync()) return;

        using var remote = new DrainRemoteFixture();
        await remote.SeedAsync();

        var teamId = await SeedTeamAsync();
        var credId = await SeedModelCredentialAsync(teamId, BrokeredProvider, "sk-drain-diff-fixture");
        var repoId = await SeedClonableRepositoryAsync(teamId, remote.RemoteUrl);
        var runId = await CreateRepoBackedRunAsync(teamId, credId, repoId);

        using var broker = new LoopbackModelCredentialBroker();
        using var shutdown = new CancellationTokenSource();
        var lifetime = new FakeHostLifetime();

        // The agent WRITES A FILE and then hangs — the shape that makes work presence mean anything. A landing with an
        // empty ChangedFiles reads to AgentWorkPresence.ShowsWork as having produced nothing, which makes the
        // supervisor's post-hoc unit grade "no-branch-or-repo with no work": a real verdict failure rather than infra.
        // The folder cannot supply the list on this path (this process's own tail already consumed those frames), so
        // the landing has to run the git capture, and this test is the thing that says it does.
        var harness = new BrokerableScriptedHarness(BrokeredProvider, $"echo '{ShutdownFactLine}'; printf 'agent wrote this\\n' > {DrainedFile}; sleep 120");
        var execution = ExecuteUntilShutdownWithAsync(runId, harness, broker, shutdown.Token, lifetime, productionCapturePlanes: true);

        await WaitUntilAsync(() => broker.HasLease(runId), TimeSpan.FromSeconds(30), "the run never opened a credential lease");
        await WaitUntilAsync(() => HandleOf(runId)?.WorkspaceBaseSha is { Length: > 0 }, TimeSpan.FromSeconds(30), "the run never stamped a repo-backed workspace on its handle, so there would be no diff for the drain to capture");
        await WaitUntilAsync(() => AgentWroteItsFile(runId), TimeSpan.FromSeconds(30), "the agent never wrote its file, so the assertion below would prove nothing");

        lifetime.Stop();
        shutdown.Cancel();
        await AwaitWithinAsync(execution, TimeSpan.FromSeconds(120), "the executor never returned after the run was stopped — every path out of it is bounded, so this means one is not honouring its token");

        using var verify = _fixture.BeginScope();
        var run = await verify.Resolve<IAgentRunService>().GetAsync(runId, CancellationToken.None);
        var result = JsonSerializer.Deserialize<AgentRunResult>(run.ResultJson!, AgentJson.Options).ShouldNotBeNull();

        result.ExitReason.ShouldBe(CodeSpace.Messages.Failures.FailureCodes.ModelCredentialLeaseLost);

        result.ChangedFiles.ShouldContain(DrainedFile,
            "the drain must capture what the agent CHANGED, or the run lands claiming it produced nothing and a deploy is graded as a failed unit of work");
        result.BaseSha.ShouldNotBeNullOrWhiteSpace("a diff is only interpretable against the sha it was taken from");
        result.Patch.ShouldContain("agent wrote this", Case.Insensitive, "the captured diff has to carry the agent's actual content, not just a file name");

        CodeSpace.Core.Services.Agents.AgentWorkPresence.ShowsWork(result).ShouldBeTrue(
            "this is the reading the supervisor's grade keys on — false here is the no-progress-budget regression this landing exists to avoid");
    }

    /// <summary>The file the drained agent writes into its clone before hanging.</summary>
    private const string DrainedFile = "agent-output.txt";

    private bool AgentWroteItsFile(Guid runId) =>
        HandleOf(runId)?.WorkspaceDirectory is { Length: > 0 } dir && File.Exists(Path.Combine(dir, DrainedFile));

    [Fact]
    public async Task A_parent_cancelling_a_brokered_run_on_a_LIVE_host_never_lands_the_lost_lease_verdict()
    {
        if (OperatingSystem.IsWindows()) return;

        var teamId = await SeedTeamAsync();
        var credId = await SeedModelCredentialAsync(teamId, BrokeredProvider, "sk-parent-cancel-fixture");
        var runId = await CreateRunWithCredentialAsync(teamId, credId);

        using var broker = new LoopbackModelCredentialBroker();
        using var parent = new CancellationTokenSource();

        // The shape three production callers actually have: AgentReviewRunner and the two benchmark cell runners hand
        // ExecuteAsync a PARENT's token, so a parent that cancels cancels this run's token while the host is
        // perfectly alive. Gating on that token would kill a healthy agent and tell its owner a worker restarted.
        var lifetime = new FakeHostLifetime();
        var execution = ExecuteUntilStoppedAsync(runId, broker, parent.Token, lifetime);

        await WaitUntilAsync(() => broker.HasLease(runId), TimeSpan.FromSeconds(30), "the run never opened a credential lease");
        await WaitUntilAsync(() => HandleOf(runId) is not null, TimeSpan.FromSeconds(30), "the run never launched an agent");

        var pid = HandleOf(runId)!.ProcessId;
        parent.Cancel();
        await AwaitWithinAsync(execution, TimeSpan.FromSeconds(120), "the executor never returned after the run was stopped — every path out of it is bounded, so this means one is not honouring its token");

        using var verify = _fixture.BeginScope();
        var run = await verify.Resolve<IAgentRunService>().GetAsync(runId, CancellationToken.None);

        run.Status.ShouldBe(AgentRunStatus.Running, "a cancelled PARENT is not a restarting host; the durable run survives its observer exactly as it always did");
        (run.Error ?? "").ShouldNotContain("worker", Shouldly.Case.Insensitive, "no worker restarted — saying one did sends an operator hunting a deploy that never happened");
        ProcessIsAlive(pid).ShouldBeTrue("the agent must still be running: a parent's cancel stops OBSERVING, it does not kill");

        try { using var p = System.Diagnostics.Process.GetProcessById(pid); p.Kill(entireProcessTree: true); } catch { /* cleanup */ }
    }

    [Fact]
    public async Task A_user_cancel_of_a_brokered_run_lands_Cancelled_and_never_the_lost_lease_verdict()
    {
        if (OperatingSystem.IsWindows()) return;

        var teamId = await SeedTeamAsync();
        var credId = await SeedModelCredentialAsync(teamId, BrokeredProvider, "sk-user-cancel-fixture");
        var runId = await CreateRunWithCredentialAsync(teamId, credId);

        using var broker = new LoopbackModelCredentialBroker();

        // The HOST is fine. Only this run is being stopped, by a person — and the executor's tear-down arm is reached
        // by an OperationCanceledException either way, because a cancel cancels the observer. Nothing about this run
        // lost its model access to a restart, so nothing here may be dressed as if it had: a verdict that blamed a
        // deploy for a user's own cancel is a lie that outlives the session, and (at an unbumped epoch) the same arm
        // would have KILLED a healthy agent on those grounds.
        // Tolerates the ownership loss too: a cancel bumps the run's fence, so this pass legitimately discovers it no
        // longer owns the run. That IS the expected shape of a cancel — what the test asserts is what got written.
        // A LIVE host — the predicate the arm gates on is false, which is the whole point: this run is being stopped
        // by a person, not by a deploy. Passing a lifetime that is not stopping is what makes the guard falsifiable;
        // with a null one the branch is unreachable for a reason unrelated to what this test is about.
        var execution = ExecuteUntilStoppedAsync(runId, broker, CancellationToken.None, new FakeHostLifetime());

        await WaitUntilAsync(() => broker.HasLease(runId), TimeSpan.FromSeconds(30), "the run never opened a credential lease");
        await WaitUntilAsync(() => HandleOf(runId) is not null, TimeSpan.FromSeconds(30), "the run never launched an agent to cancel");

        using (var scope = _fixture.BeginScope())
            (await scope.Resolve<IAgentRunService>().CancelRunningAsync(runId, "operator cancel", AgentRunAbandonCause.OperatorCancelled, CancellationToken.None)).ShouldBeTrue();

        await AwaitWithinAsync(execution, TimeSpan.FromSeconds(120), "the executor never returned after the run was stopped — every path out of it is bounded, so this means one is not honouring its token");

        using var verify = _fixture.BeginScope();
        var run = await verify.Resolve<IAgentRunService>().GetAsync(runId, CancellationToken.None);

        run.Status.ShouldBe(AgentRunStatus.Cancelled, "a person stopped this run; the executor's own tear-down must not re-grade it");
        (run.Error ?? "").ShouldNotContain("worker", Shouldly.Case.Insensitive, "no worker restarted — saying one did would send an operator hunting a deploy that never happened");

        if (run.ResultJson is { Length: > 0 } json)
            JsonSerializer.Deserialize<AgentRunResult>(json, AgentJson.Options)!.ExitReason
                .ShouldNotBe(CodeSpace.Messages.Failures.FailureCodes.ModelCredentialLeaseLost,
                    "the tear-down arm fires on EVERY OperationCanceledException — an ownership loss, a client timeout, this cancel — and only a cancelled HOST token means the lease is going away");
    }

    /// <summary>One brokered run driven to the tear-down arm. The cancel is the point of the test, so the <see cref="OperationCanceledException"/> it re-raises (the contract that leaves a non-brokered run recoverable) is expected, not a failure.</summary>
    private Task ExecuteUntilShutdownAsync(Guid runId, LoopbackModelCredentialBroker broker, CancellationToken shutdown, Microsoft.Extensions.Hosting.IHostApplicationLifetime? lifetime = null, bool productionCapturePlanes = false, IAgentRunLogCaptureBridge? logCapture = null) =>
        ExecuteUntilShutdownWithAsync(runId, new BrokerableScriptedHarness(BrokeredProvider, $"echo '{ShutdownFactLine}'; sleep 120"), broker, shutdown, lifetime, productionCapturePlanes, logCapture);

    private async Task ExecuteUntilShutdownWithAsync(Guid runId, IAgentHarness harness, LoopbackModelCredentialBroker broker, CancellationToken shutdown, Microsoft.Extensions.Hosting.IHostApplicationLifetime? lifetime, bool productionCapturePlanes, IAgentRunLogCaptureBridge? logCapture = null)
    {
        try { await ExecuteAsync(runId, harness, logCapture: logCapture, credentialBroker: broker, cancellationToken: shutdown, lifetime: lifetime, productionCapturePlanes: productionCapturePlanes); }
        catch (OperationCanceledException) { /* the worker went away — what it left behind is what this test asserts */ }
    }

    /// <summary>As above, but also tolerating the ownership loss a fence bump raises — the shape a user cancel takes, where the run is legitimately taken away from this pass rather than the pass being taken away from the host.</summary>
    private async Task ExecuteUntilStoppedAsync(Guid runId, LoopbackModelCredentialBroker broker, CancellationToken shutdown, Microsoft.Extensions.Hosting.IHostApplicationLifetime? lifetime = null)
    {
        try { await ExecuteUntilShutdownAsync(runId, broker, shutdown, lifetime); }
        catch (CodeSpace.Core.Services.Agents.Exceptions.AgentRunOwnershipLostException) { /* the cancel bumped the fence; the row it wrote is what this test asserts */ }
    }

    private SandboxHandle? HandleOf(Guid runId)
    {
        using var scope = _fixture.BeginScope();
        var json = scope.Resolve<CodeSpaceDbContext>().AgentRun.AsNoTracking().Where(r => r.Id == runId).Select(r => r.RunnerHandleJson).Single();

        return string.IsNullOrWhiteSpace(json) ? null : JsonSerializer.Deserialize<SandboxHandle>(json, AgentJson.Options);
    }

    /// <summary>A Repository row the executor's resolver can clone — a bare local repo standing in for the provider's, which a file:// URL reaches with no network and no real token.</summary>
    private async Task<Guid> SeedClonableRepositoryAsync(Guid teamId, string cloneUrl)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        var instanceId = Guid.NewGuid();
        db.ProviderInstance.Add(new ProviderInstance { Id = instanceId, TeamId = teamId, Provider = ProviderKind.GitHub, DisplayName = "local", BaseUrl = "https://local" });

        var repoId = Guid.NewGuid();
        db.Repository.Add(new Repository
        {
            Id = repoId, TeamId = teamId, ProviderInstanceId = instanceId,
            ExternalId = repoId.ToString(), NamespacePath = "org", Name = "repo", FullPath = "org/repo",
            DefaultBranch = "main", CloneUrlHttps = cloneUrl, WebUrl = "https://local/org/repo",
        });

        await db.SaveChangesAsync();
        return repoId;
    }

    private async Task<Guid> CreateRepoBackedRunAsync(Guid teamId, Guid modelCredentialId, Guid repositoryId)
    {
        using var scope = await WorkflowsTestSeed.BeginSeedOperatorScopeAsync(_fixture, teamId);
        var run = await scope.Resolve<IAgentRunService>().CreateAsync(
            new AgentTask { Goal = "scripted", Harness = "scripted-projector", Model = "test-model", ModelCredentialId = modelCredentialId, RepositoryId = repositoryId, TimeoutSeconds = 1800 },
            teamId, null, null, iterationKey: "", cancellationToken: CancellationToken.None);
        return run.Id;
    }

    /// <summary>A bare repo with one commit, standing in for the provider's remote so the executor performs a REAL clone and the handle carries a real base sha.</summary>
    private sealed class DrainRemoteFixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "cs-drain-remote-" + Guid.NewGuid().ToString("N"));
        private readonly string _bare;

        public DrainRemoteFixture()
        {
            Directory.CreateDirectory(_root);
            _bare = Path.Combine(_root, "remote.git");
        }

        public string RemoteUrl => new Uri(_bare).AbsoluteUri;

        public async Task SeedAsync()
        {
            await GitAsync(_root, "init", "--bare", "-b", "main", _bare);

            var seed = Path.Combine(_root, "seed");
            Directory.CreateDirectory(seed);
            await GitAsync(seed, "clone", _bare, seed);
            await File.WriteAllTextAsync(Path.Combine(seed, "README.md"), "base");
            await GitAsync(seed, "-c", "user.email=t@codespace.dev", "-c", "user.name=T", "add", ".");
            await GitAsync(seed, "-c", "user.email=t@codespace.dev", "-c", "user.name=T", "-c", "commit.gpgsign=false", "commit", "-m", "seed");
            await GitAsync(seed, "push", "origin", "main");
        }

        private static async Task GitAsync(string workdir, params string[] args)
        {
            var result = await new LocalProcessRunner().RunAsync(new SandboxSpec { Command = "git", Args = args, WorkingDirectory = workdir, TimeoutSeconds = 60 }, CancellationToken.None);

            if (result.Status != SandboxStatus.Success) throw new InvalidOperationException($"git {string.Join(' ', args)} failed: {result.Stderr}");
        }

        public void Dispose()
        {
            try { Directory.Delete(_root, recursive: true); } catch { /* best-effort */ }
        }
    }

    /// <summary>The harness session id the shutdown fixture's agent announces before it hangs — the input a WARM retry needs, and the thing a bare landing used to throw away.</summary>
    private const string ShutdownFactSessionId = "sess-drained-but-resumable-4c88";

    /// <summary>The line the shutdown fixture's agent prints, in the shape <c>AgentRunFactKeys.Fallback</c> reads.</summary>
    private const string ShutdownFactLine = "{\"session_id\":\"" + ShutdownFactSessionId + "\",\"usage\":{\"input_tokens\":900,\"output_tokens\":150}}";

    /// <summary>
    /// The host's own lifetime, driven by the test. The executor's tear-down arm may act ONLY on this — a cancelled
    /// job token means nothing (a parent run cancelling, a Hangfire job abort), and a fake that can be asked both ways
    /// is what makes that guard falsifiable rather than merely unreached.
    /// </summary>
    private sealed class FakeHostLifetime : Microsoft.Extensions.Hosting.IHostApplicationLifetime
    {
        private readonly CancellationTokenSource _stopping = new();
        private readonly CancellationTokenSource _stopped = new();

        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopping => _stopping.Token;
        public CancellationToken ApplicationStopped => _stopped.Token;

        public void Stop() => _stopping.Cancel();
        public void StopApplication() => _stopping.Cancel();
    }

    /// <summary>The agent's supervisor pid, asked of the OS directly — the only witness that a terminal verdict actually stopped the process rather than just writing a row about it.</summary>
    private static bool ProcessIsAlive(int pid)
    {
        try { using var process = System.Diagnostics.Process.GetProcessById(pid); return !process.HasExited; }
        catch (ArgumentException) { return false; }
        catch (InvalidOperationException) { return false; }
    }

    // ── Fixtures ──────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Await work that SHOULD finish, with a deadline and a message naming what did not. An unbounded await on an
    /// executor is how this suite killed a CI host: the blame collector reported a crash after five minutes of
    /// silence, which says nothing about the wait that produced it (Rule 12.10).
    /// </summary>
    private static async Task AwaitWithinAsync(Task work, TimeSpan budget, string what)
    {
        if (await Task.WhenAny(work, Task.Delay(budget)).ConfigureAwait(false) != work)
            throw new Xunit.Sdk.XunitException($"{what} (waited {budget.TotalSeconds}s)");

        await work.ConfigureAwait(false);   // surface its own exception rather than the timeout's
    }

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
            scope.Resolve<CodeSpace.Core.Services.Agents.AgentRunLogging.IAgentRunLogService>(),
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

        // KEEPS the line's structured root, as every real harness's parse does — AgentRunFacts reads only
        // AgentEvent.Data, so a double that dropped it would let a test pass while the executor recorded no session
        // id and no spend.
        public IReadOnlyList<AgentEvent> ParseEvents(string rawLine)
        {
            var line = rawLine.Trim();
            if (line.Length == 0) return Array.Empty<AgentEvent>();

            JsonElement? data = null;
            try { using var doc = JsonDocument.Parse(line); data = doc.RootElement.Clone(); }
            catch (JsonException) { /* a plain line carries no facts, exactly as a real harness's would not */ }

            return new[] { new AgentEvent { Kind = AgentEventKind.AssistantMessage, Text = line, Data = data } };
        }

        // Every fact production reports, via the one shared construction AgentFolderDoubleFidelityTests measures.
        public IAgentEventFolder CreateFolder() => ScriptedFolders.Result();

        public IReadOnlyList<string> SupportedProviders { get; } = new[] { provider };

        public IReadOnlyDictionary<string, string> ProjectToEnv(ResolvedModelCredential credential) =>
            new Dictionary<string, string> { [KeyEnvVar] = credential.ApiKey ?? "" };

        public IReadOnlyDictionary<string, string> ProjectBrokered(BrokeredModelCredential brokered) =>
            new Dictionary<string, string> { [BaseUrlEnvVar] = brokered.BaseUrl, [RunTokenEnvVar] = brokered.RunToken };
    }
}

