using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Agents.Credentials.Broker;
using CodeSpace.Core.Services.Agents.Exceptions;
using CodeSpace.Core.Services.Agents.Sandbox;
using CodeSpace.Core.Services.Agents.Sandbox.Runners;
using CodeSpace.Core.Services.Review;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.IntegrationTests.Workflows.Infrastructure;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Enums;
using CodeSpace.Messages.Review;
using CodeSpace.NativeLaunch;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace CodeSpace.IntegrationTests.Workflows;

/// <summary>
/// Stop, in the api/worker split: the API pod flips the row to Cancelled but cannot reach a process another host
/// launched (its kill is withheld as not-local), so the only host that CAN stop the agent is the worker that owns it —
/// and that worker learns of the cancel only as a lost fence. Driven through the REAL executor, the REAL
/// <c>AgentRunService.CancelRunningAsync</c> and the REAL <see cref="LocalProcessRunner"/> against real Postgres; the one
/// substitution is the API pod's runner, which withholds its kill exactly as the production runner does for a handle
/// minted on another host.
///
/// <para>Fidelity: HIGH. The agent is a real <c>/bin/sh</c> under the native launch protocol, and every verdict is read
/// off the OS by the product's own identity-bound oracle (<c>NativeProcess.IsAlive</c>: pid + kernel birth key + boot),
/// never off a mock. The script stays silent until the test drops a <c>go</c> file into its workspace, so the fence
/// loss is observed at a point the test chooses: either the executor's next FENCED WRITE (the checkpoint the first
/// output line triggers) or the observer being torn down (the arm a heartbeat's loss and a worker shutdown reach).</para>
///
/// <para>The reclaim rows pin the behaviour this must NOT change: a lost fence that is a reclaim leaves the live agent
/// and its clone for the worker that re-attaches it.</para>
/// </summary>
public partial class AgentRunExecutorTests
{
    private const string GoFile = "go";
    private const string PingFile = "ping";

    [Theory]
    [InlineData(true, true)]     // cancelled; the loss surfaces at the next fenced write
    [InlineData(true, false)]    // cancelled; the observer is torn down before anything is written
    [InlineData(false, true)]    // reclaimed for a re-attach; the loss surfaces at the next fenced write
    [InlineData(false, false)]   // reclaimed for a re-attach; the observer is torn down
    public async Task A_lost_fence_kills_the_owned_agent_only_when_the_run_was_cancelled(bool cancelled, bool observedByFencedWrite)
    {
        if (OperatingSystem.IsWindows()) return;

        var teamId = await SeedTeamAsync();
        var runId = await CreateScriptedRunAsync(teamId, timeoutSeconds: 300);
        var scratch = Path.Combine(Path.GetTempPath(), $"codespace-scratch-{runId:N}");   // ScratchWorkspaceHandle.Create's own run-keyed path
        var harness = new ScriptedHarness($"while [ ! -f {GoFile} ]; do sleep 0.2; done; while :; do echo tick; sleep 0.5; done");
        using var observer = new CancellationTokenSource();
        SandboxHandle? handle = null;

        try
        {
            var execution = ExecuteUntilFenceLostAsync(runId, harness, observer.Token);

            await WaitUntilAsync(() => HandleOf(runId) is not null, TimeSpan.FromSeconds(30), "the run never persisted a durable handle, so no agent was launched to stop — check the scripted harness and the native runner host");
            handle = HandleOf(runId)!;
            var agent = handle.NativeLaunch.ShouldNotBeNull("a durable launch carries its native execution identity").Execution.ShouldNotBeNull();

            NativeProcess.IsAlive(agent).ShouldBeTrue($"precondition: the agent is running before its run is taken away — {ProcessLiveness.Describe(handle)}");
            Directory.Exists(scratch).ShouldBeTrue("precondition: the executor prepared the run's scratch workspace");

            if (cancelled) await CancelOnAnotherHostAsync(runId);
            else (await ReserveReattachAfterLapseAsync(runId)).ShouldNotBeNull("the reclaim must win the reservation on a lapsed Running run");

            NativeProcess.IsAlive(agent).ShouldBeTrue($"the flip alone must not have reached the agent — that is the defect's premise, and without it this test proves nothing — {ProcessLiveness.Describe(handle)}");

            if (observedByFencedWrite) await File.WriteAllTextAsync(Path.Combine(scratch, GoFile), "");
            else observer.Cancel();

            await AwaitWithinAsync(execution, TimeSpan.FromSeconds(60), "the executor never returned after losing its run — every path out of it is bounded, so one of them is not honouring its token");

            using var verify = _fixture.BeginScope();
            var run = await verify.Resolve<CodeSpaceDbContext>().AgentRun.AsNoTracking().SingleAsync(r => r.Id == runId);

            if (cancelled)
            {
                await WaitUntilAsync(() => !NativeProcess.IsAlive(agent), TimeSpan.FromSeconds(10), $"the owning worker observed its run was CANCELLED and left the agent running — it is the only host that can stop it; diagnose with {ProcessLiveness.Describe(handle)}");
                Directory.Exists(scratch).ShouldBeFalse("a cancelled run whose agent was stopped owes nobody its clone — it must be torn down, not kept for a re-attach that will never come");
                run.Status.ShouldBe(AgentRunStatus.Cancelled, "the executor's tear-down must not write over the operator's cancel");
                run.Error.ShouldBe("operator cancel");
            }
            else
            {
                NativeProcess.IsAlive(agent).ShouldBeTrue($"a RECLAIM hands the live agent to the worker that re-attaches it — killing it here destroys a run a restart no longer has to cost — {ProcessLiveness.Describe(handle)}");
                Directory.Exists(scratch).ShouldBeTrue("the re-attach reuses the surviving clone, so a reclaim must leave it in place");
                run.Status.ShouldBe(AgentRunStatus.Running, "a reclaim keeps the run Running for its successor");
            }
        }
        finally
        {
            if (handle is not null)
            {
                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                await new LocalProcessRunner().TerminateAsync(handle, cleanup.Token);
            }

            if (Directory.Exists(scratch)) Directory.Delete(scratch, recursive: true);
        }
    }

    /// <summary>
    /// The same Stop against a run a RE-ATTACH owns — the shape after a worker restart, when the agent outlived the
    /// worker that launched it and a second pass on the same host took it over. That pass is its agent's owner now, and
    /// a cancel of its attempt leaves nobody else able to stop it. Both of its tear-down arms, as for the launch.
    /// </summary>
    [Theory]
    [InlineData(true)]    // the loss surfaces at the re-attach's next fenced write
    [InlineData(false)]   // the re-attach's observer is torn down before anything is written
    public async Task A_reattached_owner_kills_its_agent_when_the_run_is_cancelled(bool observedByFencedWrite)
    {
        if (OperatingSystem.IsWindows()) return;

        var teamId = await SeedTeamAsync();
        var runId = await CreateScriptedRunAsync(teamId, timeoutSeconds: 300);
        var scratch = Path.Combine(Path.GetTempPath(), $"codespace-scratch-{runId:N}");
        var harness = new ScriptedHarness($"while [ ! -f {PingFile} ]; do sleep 0.2; done; echo pong; while [ ! -f {GoFile} ]; do sleep 0.2; done; while :; do echo tick; sleep 0.5; done");
        using var firstWorker = new CancellationTokenSource();
        using var nextWorker = new CancellationTokenSource();
        using var nextWorkerBroker = new LoopbackModelCredentialBroker();
        SandboxHandle? handle = null;

        try
        {
            var launch = ExecuteUntilFenceLostAsync(runId, harness, firstWorker.Token);

            await WaitUntilAsync(() => HandleOf(runId) is not null, TimeSpan.FromSeconds(30), "the run never persisted a durable handle, so no agent was launched to re-attach to");
            handle = HandleOf(runId)!;
            var agent = handle.NativeLaunch.ShouldNotBeNull().Execution.ShouldNotBeNull();

            firstWorker.Cancel();
            await AwaitWithinAsync(launch, TimeSpan.FromSeconds(60), "the first worker never returned after it was torn down");

            NativeProcess.IsAlive(agent).ShouldBeTrue($"precondition: the agent outlived the worker that launched it — {ProcessLiveness.Describe(handle)}");

            var reservation = (await ReserveReattachAfterLapseAsync(runId)).ShouldNotBeNull("the reclaim must win the reservation on a lapsed Running run");
            var reattach = ReattachUntilStoppedAsync(reservation, harness, nextWorkerBroker, nextWorker.Token);

            // Sync on the re-attach actually TAILING — the agent's one line checkpointed under the re-attach's own fence
            // — so the cancel can only land on a pass that is observing, never in its prelude.
            await File.WriteAllTextAsync(Path.Combine(scratch, PingFile), "");
            await WaitUntilAsync(() => HandleOf(runId)!.StdoutOffset > 0, TimeSpan.FromSeconds(30), "the re-attach never checkpointed the agent's output, so it never started observing the run it reserved");

            await CancelOnAnotherHostAsync(runId);
            NativeProcess.IsAlive(agent).ShouldBeTrue($"the API pod's flip alone must not have reached the agent — {ProcessLiveness.Describe(handle)}");

            if (observedByFencedWrite) await File.WriteAllTextAsync(Path.Combine(scratch, GoFile), "");
            else nextWorker.Cancel();

            await AwaitWithinAsync(reattach, TimeSpan.FromSeconds(60), "the re-attaching executor never returned after losing its run");

            await WaitUntilAsync(() => !NativeProcess.IsAlive(agent), TimeSpan.FromSeconds(10), $"the re-attached owner observed its run was CANCELLED and left the agent running; diagnose with {ProcessLiveness.Describe(handle)}");
        }
        finally
        {
            if (handle is not null)
            {
                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                await new LocalProcessRunner().TerminateAsync(handle, cleanup.Token);
            }

            if (Directory.Exists(scratch)) Directory.Delete(scratch, recursive: true);
        }
    }

    /// <summary>
    /// A cancel that lands inside a LATER round's launch window — the round's process started, its handle not yet on the
    /// row — must not cost that process its clone. The row still names the previous round's finished process there, so
    /// the kill of the recorded handle answers AlreadyGone; the pass cleared its acknowledged launch when the new one
    /// began, so it knows that answer proves nothing about the clone, and keeps it. Staged deterministically: an Improve
    /// critic forces a second round, and a runner starts that round's REAL process and then holds its handle back until
    /// the observer is torn down.
    /// </summary>
    [Fact]
    public async Task A_cancel_inside_a_later_rounds_launch_window_keeps_the_clone_that_rounds_process_runs_in()
    {
        if (OperatingSystem.IsWindows()) return;

        var teamId = await SeedTeamAsync();
        var runId = await CreateImproveRunAsync(teamId);
        var scratch = Path.Combine(Path.GetTempPath(), $"codespace-scratch-{runId:N}");
        var harness = new ScriptedHarness("if [ -f round0-done ]; then sleep 300; else touch round0-done; echo a draft answer; fi");
        var runner = new HeldSecondLaunchRunner();
        using var observer = new CancellationTokenSource();

        try
        {
            var execution = ExecuteUntilFenceLostAsync(runId, harness, observer.Token, new SandboxRunnerRegistry([runner]), new FlaggingCritic());

            var second = await runner.SecondLaunchStarted.Task.WaitAsync(TimeSpan.FromSeconds(60));
            var agent = second.NativeLaunch.ShouldNotBeNull().Execution.ShouldNotBeNull();

            NativeProcess.IsAlive(agent).ShouldBeTrue($"precondition: the revise round's process is running in the clone — {ProcessLiveness.Describe(second)}");
            HandleOf(runId).ShouldNotBeNull("precondition: round 0 acknowledged its launch").SpoolDirectory.ShouldNotBe(second.SpoolDirectory, "precondition: the row still names round 0 — the round-1 handle never reached it");

            await CancelOnAnotherHostAsync(runId);
            observer.Cancel();

            await AwaitWithinAsync(execution, TimeSpan.FromSeconds(60), "the executor never returned after its observer was torn down mid-launch");

            Directory.Exists(scratch).ShouldBeTrue("the recorded agent's AlreadyGone is about round 0; round 1's process is still working in this clone, so deleting it here pulls the directory out from under a live agent");
        }
        finally
        {
            if (runner.SecondLaunchStarted.Task.IsCompletedSuccessfully)
            {
                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                await new LocalProcessRunner().TerminateAsync(await runner.SecondLaunchStarted.Task, cleanup.Token);
            }

            if (Directory.Exists(scratch)) Directory.Delete(scratch, recursive: true);
        }
    }

    private async Task<Guid> CreateImproveRunAsync(Guid teamId)
    {
        using var scope = await WorkflowsTestSeed.BeginSeedOperatorScopeAsync(_fixture, teamId);
        var run = await scope.Resolve<IAgentRunService>().CreateAsync(new AgentTask { Goal = "scripted", Harness = "scripted", Model = "test-model", TimeoutSeconds = 300, OutputReviewMode = ReviewMode.Improve, MaxReviseRounds = 1 }, teamId, null, null, iterationKey: "", cancellationToken: CancellationToken.None);

        return run.Id;
    }

    /// <summary>The Improve reviewer that buys the run its second round: it flags every output with a critique the revise loop can feed back.</summary>
    private sealed class FlaggingCritic : IStructuredCritic
    {
        public Task<CriticVerdict> ReviewAsync(CriticRequest request, Guid teamId, Guid? reviewerModelId, CancellationToken cancellationToken) =>
            Task.FromResult(new CriticVerdict { Mode = request.Mode, Approved = false, Rationale = "not there yet", Critique = "revise the answer" });
    }

    /// <summary>
    /// The real local runner, except that its SECOND launch starts the process and then never hands the handle back — it
    /// waits on the executor's own token, so the round is torn down exactly between "a process exists" and "the row
    /// names it". Every other member is the real runner's.
    /// </summary>
    private sealed class HeldSecondLaunchRunner : ISandboxRunner, ISandboxDurableRunner
    {
        private readonly LocalProcessRunner _inner = new();
        private int _launches;

        public TaskCompletionSource<SandboxHandle> SecondLaunchStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public string Kind => LocalProcessRunner.LocalKind;

        public async Task<SandboxHandle> LaunchAsync(SandboxSpec spec, string spoolKey, CancellationToken cancellationToken)
        {
            var handle = await _inner.LaunchAsync(spec, spoolKey, cancellationToken);

            if (Interlocked.Increment(ref _launches) == 1) return handle;

            SecondLaunchStarted.TrySetResult(handle);
            await Task.Delay(Timeout.Infinite, cancellationToken);

            return handle;
        }

        public Task<SandboxResult> AttachAsync(SandboxHandle handle, Func<SandboxOutputFrame, CancellationToken, Task> onStdoutFrame, CancellationToken cancellationToken, Func<long, CancellationToken, Task>? onCheckpoint = null) => _inner.AttachAsync(handle, onStdoutFrame, cancellationToken, onCheckpoint);
        public Task<SandboxProbe> ProbeAsync(SandboxHandle handle, CancellationToken cancellationToken) => _inner.ProbeAsync(handle, cancellationToken);
        public Task<SandboxTerminateResult> TerminateAsync(SandboxHandle handle, CancellationToken cancellationToken) => _inner.TerminateAsync(handle, cancellationToken);
        public Task<SandboxResult> RunAsync(SandboxSpec spec, CancellationToken cancellationToken) => throw new NotSupportedException("the executor must take the durable path");
    }

    /// <summary>One executor pass driven until its run is taken away. Losing the run is the point of the test, so the two exits that report it — the lost fence, and the observer's own cancellation — are expected rather than failures.</summary>
    private Task ExecuteUntilFenceLostAsync(Guid runId, IAgentHarness harness, CancellationToken observer, ISandboxRunnerRegistry? runners = null, IStructuredCritic? critic = null) =>
        Task.Run(async () =>
        {
            try { await ExecuteAsync(runId, harness, runners: runners, critic: critic, cancellationToken: observer); }
            catch (AgentRunOwnershipLostException) { /* the fence moved; what the pass left behind is what the test asserts */ }
            catch (OperationCanceledException) { /* the observer was torn down; likewise */ }
        });

    /// <summary>
    /// The operator's Stop as the API pod performs it: the REAL <c>CancelRunningAsync</c> CAS, from a pod whose runner
    /// cannot reach the process (<see cref="UnreachableProcessRunner"/>). Asserts the pod really did try its kill and was
    /// refused, because a test in which the API pod's own kill succeeded would pass without the fix.
    /// </summary>
    private async Task CancelOnAnotherHostAsync(Guid runId)
    {
        using var apiPod = _fixture.BeginScope();
        using var apiPodBroker = new LoopbackModelCredentialBroker();   // the API pod launches nothing, so it holds no lease
        var unreachable = new UnreachableProcessRunner();

        (await BuildCancelService(apiPod, unreachable, apiPodBroker).CancelRunningAsync(runId, "operator cancel", AgentRunAbandonCause.OperatorCancelled, CancellationToken.None))
            .ShouldBeTrue("the cancel must win the Running → Cancelled CAS at the owning worker's epoch");

        unreachable.KillAttempted.ShouldBeTrue("the API pod must have tried — and been refused — its own kill");
    }

    /// <summary>The API pod's view of a worker's process: every durable terminate is withheld as <see cref="SandboxTerminateOutcome.SkippedNotLocal"/>, the answer <c>LocalProcessRunner</c> gives for a launch minted on another host.</summary>
    private sealed class UnreachableProcessRunner : ISandboxRunner, ISandboxDurableRunner
    {
        public bool KillAttempted { get; private set; }

        public string Kind => SandboxKinds.Local;

        public Task<SandboxTerminateResult> TerminateAsync(SandboxHandle handle, CancellationToken cancellationToken)
        {
            KillAttempted = true;

            return Task.FromResult(SandboxTerminateResult.Skipped(SandboxTerminateOutcome.SkippedNotLocal, $"the launch was minted by host '{handle.LaunchHost}'; this is the API pod"));
        }

        public Task<SandboxResult> RunAsync(SandboxSpec spec, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<SandboxHandle> LaunchAsync(SandboxSpec spec, string spoolKey, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<SandboxResult> AttachAsync(SandboxHandle handle, Func<SandboxOutputFrame, CancellationToken, Task> onStdoutFrame, CancellationToken cancellationToken, Func<long, CancellationToken, Task>? onCheckpoint = null) => throw new NotSupportedException();
        public Task<SandboxProbe> ProbeAsync(SandboxHandle handle, CancellationToken cancellationToken) => Task.FromResult(new SandboxProbe { State = SandboxRunState.Indeterminate });
    }
}
