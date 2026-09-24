using System.Text.Json;
using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Agents.Sandbox;
using CodeSpace.Core.Services.Agents.Sandbox.Runners;
using CodeSpace.Core.Services.RunData;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.IntegrationTests.Workflows.Infrastructure;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Enums;
using CodeSpace.NativeLaunch;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Shouldly;

namespace CodeSpace.IntegrationTests.Workflows;

/// <summary>
/// One database fault while the executor OBSERVES a live agent — the connection reset, pool timeout, 57P01 admin
/// shutdown or failover blip that a single output poll can land on. Each poll persists the agent's events and its
/// spool offset, and a fault there used to escape the tail loop: the executor's catch-all landed the healthy run
/// Failed(executor-error) and its finally deleted the workspace while the detached agent kept working inside it.
///
/// <para>Fidelity: HIGH. The real executor drives a real <c>/bin/sh</c> agent through the real
/// <see cref="LocalProcessRunner"/> against real Postgres, and the fault is REAL: the test pins the executor's scope to
/// one physical connection and has the server terminate that backend with <c>pg_terminate_backend</c>, so the write
/// the next poll makes fails exactly as it does in production — no injected exception type. The agent stays silent
/// until the test drops a file into its workspace, so the kill lands between two polls the test chooses.</para>
/// </summary>
public partial class AgentRunExecutorTests
{
    private const string ReleaseFile = "release";
    private const string FinishFile = "finish";

    [Fact]
    public async Task A_connection_killed_under_the_observer_costs_no_event_and_leaves_the_verdict_to_the_agent()
    {
        if (OperatingSystem.IsWindows()) return;

        var teamId = await SeedTeamAsync();
        var runId = await CreateScriptedRunAsync(teamId, timeoutSeconds: 300);
        var scratch = ScratchOf(runId);
        var harness = new ScriptedHarness($"printf 'before-1\\nbefore-2\\n'; until [ -f {ReleaseFile} ]; do sleep 0.1; done; printf 'after-1\\nafter-2\\n'; until [ -f {FinishFile} ]; do sleep 0.1; done");
        SandboxHandle? handle = null;

        try
        {
            using var scope = _fixture.BeginScope();
            var observerBackend = await PinObserverConnectionAsync(scope);
            var runs = new InstrumentedAgentRunService(scope.Resolve<IAgentRunService>());
            var execution = Task.Run(() => NewExecutor(scope, harness, runs: runs).ExecuteAsync(runId, CancellationToken.None));

            await WaitUntilAsync(() => EventTextsOf(runId).Count == 2, TimeSpan.FromSeconds(30), "the agent's first two lines never became durable, so the observer never started tailing — check the scripted harness and the native runner host");
            handle = HandleOf(runId).ShouldNotBeNull("a tailing observer has acknowledged its launch");
            var agent = handle.NativeLaunch.ShouldNotBeNull("a durable launch carries its native execution identity").Execution.ShouldNotBeNull();

            await TerminateBackendAsync(observerBackend);
            await File.WriteAllTextAsync(Path.Combine(scratch, ReleaseFile), "");

            await WaitUntilAsync(() => EventTextsOf(runId).Count == 4, TimeSpan.FromSeconds(30), "the lines the agent printed after its observer's connection was killed never became durable — the poll that hit the dead connection gave its batch up instead of offering it again");

            runs.OwnedAppendFaults.ShouldContain(fault => RaisedByNpgsql(fault), "the kill must have reached the observer's own append; without that fault this test proves nothing about surviving one");
            RunOf(runId).Status.ShouldBe(AgentRunStatus.Running, "one refused write is not the agent's verdict — the run must still be observing it");
            Directory.Exists(scratch).ShouldBeTrue("the agent is still working in its clone, so nothing may have deleted it");
            NativeProcess.IsAlive(agent).ShouldBeTrue($"the agent must be untouched by its observer's database fault — {ProcessLiveness.Describe(handle)}");

            await File.WriteAllTextAsync(Path.Combine(scratch, FinishFile), "");
            await AwaitWithinAsync(execution, TimeSpan.FromSeconds(60), "the executor never returned after its agent finished");

            RunOf(runId).Status.ShouldBe(AgentRunStatus.Succeeded, "only the agent's own exit decides the run, and it exited 0");
            EventTextsOf(runId).ShouldBe(["before-1", "before-2", "after-1", "after-2"], "every line lands exactly once — none lost to the failed attempt, none duplicated by the retry");
        }
        finally
        {
            await TerminateAgentAsync(handle ?? HandleOf(runId));
            if (Directory.Exists(scratch)) Directory.Delete(scratch, recursive: true);
        }
    }

    /// <summary>
    /// A database that never answers one batch for longer than the retry bound: the batch is given up as a capture gap —
    /// readable through the run's own operator summary, so the REAL gap guard admitted it — the observation goes on,
    /// and the agent's own exit decides the run, whichever way it goes. The outage is injected at the decorator
    /// (a transient Npgsql fault the offer never survives), and the executor runs on a clock that shortens every wait,
    /// so the committed retry schedule runs in a fraction of a second.
    /// </summary>
    [Theory]
    [InlineData(0, AgentRunStatus.Succeeded, "completed")]
    [InlineData(3, AgentRunStatus.Failed, "non-zero-exit")]
    public async Task A_batch_the_database_never_took_is_a_named_gap_and_the_agent_still_decides_the_run(int exitCode, AgentRunStatus verdict, string exitReason)
    {
        if (OperatingSystem.IsWindows()) return;

        var teamId = await SeedTeamAsync();
        var runId = await CreateScriptedRunAsync(teamId, timeoutSeconds: 300);
        var scratch = ScratchOf(runId);
        var harness = new ScriptedHarness($"printf 'lost-1\\nlost-2\\n'; until [ -f {ReleaseFile} ]; do sleep 0.1; done; printf 'after\\n'; exit {exitCode}");
        using var healed = new CancellationTokenSource();

        try
        {
            using var scope = _fixture.BeginScope();
            var runs = new InstrumentedAgentRunService(scope.Resolve<IAgentRunService>()) { FaultBeforeOwnedAppend = () => healed.IsCancellationRequested ? null : ConnectionReset() };
            var execution = Task.Run(() => NewExecutor(scope, harness, runs: runs, completeness: scope.Resolve<IRunDataCompletenessWriter>(), clock: new HurriedClock()).ExecuteAsync(runId, CancellationToken.None));

            await WaitUntilAsync(() => SemanticEventGapsOf(runId, teamId).Count == 1, TimeSpan.FromSeconds(30), "the batch the database never took was not named as a capture gap — either the retry never gave it up, or the gap guard refused the row");
            runs.BatchedCalls.ShouldBe(ObserverWriteRetry.MaxAttempts, "the unanswered batch is offered exactly as often as the committed bound allows, and not again");
            RunOf(runId).Status.ShouldBe(AgentRunStatus.Running, "an unanswered write is not the agent's verdict — the observer must still be watching it");

            healed.Cancel();
            await File.WriteAllTextAsync(Path.Combine(scratch, ReleaseFile), "");
            await AwaitWithinAsync(execution, TimeSpan.FromSeconds(60), "the executor never returned after its agent exited");

            var run = RunOf(runId);
            run.Status.ShouldBe(verdict, $"the agent exited {exitCode}, and only that may decide the run");
            JsonSerializer.Deserialize<AgentRunResult>(run.ResultJson.ShouldNotBeNull(), AgentJson.Options).ShouldNotBeNull().ExitReason.ShouldBe(exitReason, "the verdict is the harness's own, never the executor-error a database fault used to stamp");
            EventTextsOf(runId).ShouldBe(["after"], "the given-up batch is the gap's; everything after the outage lands");

            var gap = SemanticEventGapsOf(runId, teamId).ShouldHaveSingleItem();
            (gap.Reason, gap.RangeKind, gap.Resolution).ShouldBe(("RemoteUnavailable", "Time", "Open"));
            gap.ReasonDetail.ShouldNotBeNull().ShouldContain("2 normalized event(s)");
            Directory.Exists(scratch).ShouldBeFalse("the agent exited, so the run's terminal cleanup owns its clone as it always did");
        }
        finally
        {
            healed.Cancel();
            await TerminateAgentAsync(HandleOf(runId));
            if (Directory.Exists(scratch)) Directory.Delete(scratch, recursive: true);
        }
    }

    /// <summary>
    /// The fault a retry must never duplicate: the batch COMMITTED and the acknowledgement was lost on the way back.
    /// The re-offer carries the same row ids, so the real append keeps the rows it already has and succeeds.
    /// </summary>
    [Fact]
    public async Task A_batch_that_committed_before_its_acknowledgement_was_lost_lands_exactly_once()
    {
        if (OperatingSystem.IsWindows()) return;

        var teamId = await SeedTeamAsync();
        var runId = await CreateScriptedRunAsync(teamId, timeoutSeconds: 300);
        var acknowledgementsLost = 0;
        var harness = new ScriptedHarness("printf 'one\\ntwo\\n'; sleep 0.5; printf 'three\\n'");

        using (var scope = _fixture.BeginScope())
        {
            var runs = new InstrumentedAgentRunService(scope.Resolve<IAgentRunService>()) { FaultAfterOwnedAppend = () => Interlocked.Exchange(ref acknowledgementsLost, 1) == 0 ? ConnectionReset() : null };

            await AwaitWithinAsync(NewExecutor(scope, harness, runs: runs).ExecuteAsync(runId, CancellationToken.None), TimeSpan.FromSeconds(60), "the executor never returned");
        }

        acknowledgementsLost.ShouldBe(1, "precondition: one committed batch lost its acknowledgement");
        RunOf(runId).Status.ShouldBe(AgentRunStatus.Succeeded);
        EventTextsOf(runId).ShouldBe(["one", "two", "three"], "the re-offered batch is kept once — the rows it already has are not inserted again");
    }

    /// <summary>
    /// The workspace rule for a failure the executor-error arm DOES land: a fault that is a verdict about the write (not
    /// transient) still fails the run, but while the runner's identity-checked probe says the agent is running, its
    /// clone is left for the workspace janitor instead of being deleted under it. Once the probe says the agent has
    /// exited, the same failure releases the clone as it always did.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_run_failed_by_its_observer_keeps_the_clone_exactly_while_its_agent_is_alive(bool agentAlive)
    {
        if (OperatingSystem.IsWindows()) return;

        var teamId = await SeedTeamAsync();
        var runId = await CreateScriptedRunAsync(teamId, timeoutSeconds: 300);
        var scratch = ScratchOf(runId);
        var harness = new ScriptedHarness(agentAlive ? $"printf 'one\\n'; until [ -f {FinishFile} ]; do sleep 0.1; done" : "printf 'one\\n'; sleep 0.5");
        SandboxHandle? handle = null;

        try
        {
            using (var scope = _fixture.BeginScope())
            {
                var runs = new InstrumentedAgentRunService(scope.Resolve<IAgentRunService>()) { ThrowOnAppendEventsCall = 1, BeforeOwnedAppendAsync = async _ => { if (!agentAlive) await WaitUntilExitedAsync(runId); } };

                await AwaitWithinAsync(NewExecutor(scope, harness, runs: runs).ExecuteAsync(runId, CancellationToken.None), TimeSpan.FromSeconds(60), "the executor never returned after its observer failed");
            }

            handle = HandleOf(runId).ShouldNotBeNull("the failure landed after the launch was acknowledged");
            var run = RunOf(runId);
            run.Status.ShouldBe(AgentRunStatus.Failed, "a verdict about the write still fails the run");
            run.Error.ShouldNotBeNull().ShouldContain(InstrumentedAgentRunService.AppendEventsFaultMessage);

            if (agentAlive)
            {
                NativeProcess.IsAlive(handle.NativeLaunch.ShouldNotBeNull().Execution.ShouldNotBeNull()).ShouldBeTrue($"precondition: the agent outlived its observer's failure — {ProcessLiveness.Describe(handle)}");
                Directory.Exists(scratch).ShouldBeTrue("the probe says the agent is running in its clone, so deleting the clone would pull the directory out from under it");
            }
            else
            {
                Directory.Exists(scratch).ShouldBeFalse("the probe says the agent has exited, so nothing stands in the clone and the failed run's cleanup owns it");
            }
        }
        finally
        {
            await TerminateAgentAsync(handle ?? HandleOf(runId));
            if (Directory.Exists(scratch)) Directory.Delete(scratch, recursive: true);
        }
    }

    /// <summary>
    /// The workspace rule must survive the completion failing AFTER its terminal write — the notifier's read hitting a
    /// database blip, or the worker being torn down between the write and the notify. The run is then terminal and
    /// owned by this pass, so the ownership check alone would hand the clone to the cleanup: the probe has to have been
    /// asked before the write, or a live agent's clone is deleted without anyone asking.
    /// </summary>
    [Theory]
    [InlineData(true, false)]    // agent alive; the notifier's read hits a dropped connection
    [InlineData(true, true)]     // agent alive; the worker is torn down between the terminal write and the notify
    [InlineData(false, false)]   // agent exited; the same blip, and nothing stands in the clone
    public async Task A_completion_that_fails_after_its_terminal_write_keeps_a_live_agents_clone(bool agentAlive, bool workerTornDown)
    {
        if (OperatingSystem.IsWindows()) return;

        var teamId = await SeedTeamAsync();
        var runId = await CreateScriptedRunAsync(teamId, timeoutSeconds: 300);
        var scratch = ScratchOf(runId);
        var harness = new ScriptedHarness(agentAlive ? $"printf 'one\\n'; until [ -f {FinishFile} ]; do sleep 0.1; done" : "printf 'one\\n'; sleep 0.5");
        using var worker = new CancellationTokenSource();
        var notifier = new NotifierFailingAfterTerminalWrite(workerTornDown ? worker : null);

        try
        {
            Exception? escaped;

            using (var scope = _fixture.BeginScope())
            {
                var runs = new InstrumentedAgentRunService(scope.Resolve<IAgentRunService>()) { ThrowOnAppendEventsCall = 1, BeforeOwnedAppendAsync = async _ => { if (!agentAlive) await WaitUntilExitedAsync(runId); } };

                escaped = await Record.ExceptionAsync(() => AwaitWithinAsync(NewExecutor(scope, harness, notifier: notifier, runs: runs).ExecuteAsync(runId, worker.Token), TimeSpan.FromSeconds(60), "the executor never returned after its completion failed"));
            }

            notifier.Calls.ShouldBe(1, "precondition: the completion reached its notifier, so the terminal write had landed");
            escaped.ShouldNotBeNull("precondition: the completion's failure escaped the executor-error arm").ShouldBeAssignableTo(workerTornDown ? typeof(OperationCanceledException) : typeof(NpgsqlException));
            RunOf(runId).Status.ShouldBe(AgentRunStatus.Failed, "precondition: the run is terminal and owned by this pass — the state in which the ownership check alone releases the clone");

            var handle = HandleOf(runId).ShouldNotBeNull();

            if (agentAlive)
            {
                NativeProcess.IsAlive(handle.NativeLaunch.ShouldNotBeNull().Execution.ShouldNotBeNull()).ShouldBeTrue($"precondition: the agent outlived its observer's failure — {ProcessLiveness.Describe(handle)}");
                Directory.Exists(scratch).ShouldBeTrue("the probe said the agent is running in its clone before the terminal write, so a completion failing after that write must not delete the clone under it");
            }
            else
            {
                Directory.Exists(scratch).ShouldBeFalse("the probe said the agent has exited, so the failed run's cleanup still owns its clone");
            }
        }
        finally
        {
            await TerminateAgentAsync(HandleOf(runId));
            if (Directory.Exists(scratch)) Directory.Delete(scratch, recursive: true);
        }
    }

    /// <summary>The completion notifier failing once the terminal write has landed: its read hits a dropped connection, or — given <paramref name="worker"/> — the worker is torn down between the write and the notify.</summary>
    private sealed class NotifierFailingAfterTerminalWrite(CancellationTokenSource? worker) : IAgentRunCompletionNotifier
    {
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);

        public Task NotifyCompletedAsync(Guid agentRunId, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _calls);

            if (worker is null) return Task.FromException(ConnectionReset());

            worker.Cancel();
            return Task.FromCanceled(cancellationToken);
        }
    }

    /// <summary>
    /// The other boundary of the workspace rule: a failure raised before any launch began has nothing that could be
    /// standing in the clone, so the executor-error arm releases it exactly as before — keeping it would leak the clone
    /// of every run that fails before its agent starts. The clone is seeded with a marker first, so its absence
    /// afterwards proves the cleanup ran rather than that the directory never existed.
    /// </summary>
    [Fact]
    public async Task A_run_failed_before_any_launch_still_releases_its_clone()
    {
        if (OperatingSystem.IsWindows()) return;

        var teamId = await SeedTeamAsync();
        var runId = await CreateResumeRunAsync(teamId, transcriptArtifactId: Guid.NewGuid());
        var scratch = ScratchOf(runId);

        Directory.CreateDirectory(scratch);
        await File.WriteAllTextAsync(Path.Combine(scratch, "marker"), "");

        try
        {
            await ExecuteAsync(runId, new ScriptedHarness("printf 'must-not-launch\\n'"));

            RunOf(runId).Status.ShouldBe(AgentRunStatus.Failed, "precondition: a required transcript that does not exist fails the run in the executor-error arm");
            HandleOf(runId).ShouldBeNull("precondition: the failure came before any launch");
            Directory.Exists(scratch).ShouldBeFalse("no launch began, so nothing can be standing in the clone and the failed run's cleanup owns it");
        }
        finally
        {
            if (Directory.Exists(scratch)) Directory.Delete(scratch, recursive: true);
        }
    }

    /// <summary>Best-effort: a test that failed early must not leave its agent looping in a directory nobody will release.</summary>
    private static async Task TerminateAgentAsync(SandboxHandle? handle)
    {
        if (handle is null) return;

        using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await new LocalProcessRunner().TerminateAsync(handle, cleanup.Token);
    }

    /// <summary>Hold the failing append until the runner's own probe says the agent has exited — the moment the executor-error arm's question has the answer "nothing is running".</summary>
    private async Task WaitUntilExitedAsync(Guid runId)
    {
        var handle = HandleOf(runId).ShouldNotBeNull("an append only happens after the launch was acknowledged");

        await WaitUntilAsync(() => new LocalProcessRunner().ProbeAsync(handle, CancellationToken.None).GetAwaiter().GetResult().State == SandboxRunState.Exited, TimeSpan.FromSeconds(30), $"the agent never exited — {ProcessLiveness.Describe(handle)}");
    }

    /// <summary>The fault a dropped connection surfaces as — the shape Npgsql raises when the socket dies under a command.</summary>
    private static NpgsqlException ConnectionReset() => new("Exception while reading from stream", new IOException("Connection reset by peer"));

    /// <summary>The gaps the run's own operator summary reads back — the production reader, so a row the gap guard refused can never pass as recorded.</summary>
    private List<CodeSpace.Messages.Dtos.Agents.AgentRunCaptureGapSummary> SemanticEventGapsOf(Guid runId, Guid teamId)
    {
        using var scope = _fixture.BeginScope();
        var summary = scope.Resolve<IAgentRunService>().GetSummaryForTeamAsync(runId, teamId, CancellationToken.None).GetAwaiter().GetResult().ShouldNotBeNull();

        return summary.CaptureGaps.Items.Where(gap => gap.SubjectKind == CodeSpace.Messages.Contracts.WorkflowRunDataOwnerKinds.SemanticEvent).ToList();
    }

    /// <summary>The system clock with every wait shortened fifty-fold — the committed retry schedule, run on the timer machinery production uses, fast enough for a suite. Timestamps stay real, so no budget is spent early.</summary>
    private sealed class HurriedClock : TimeProvider
    {
        private const int Factor = 50;

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) => System.CreateTimer(callback, state, Hurried(dueTime), Hurried(period));

        private static TimeSpan Hurried(TimeSpan span) => span == Timeout.InfiniteTimeSpan ? span : span / Factor;
    }

    /// <summary>The run-keyed scratch directory <c>ScratchWorkspaceHandle.Create</c> gives a repo-less run — the clone the agent's working directory is.</summary>
    private static string ScratchOf(Guid runId) => Path.Combine(Path.GetTempPath(), $"codespace-scratch-{runId:N}");

    /// <summary>
    /// Open the scope's connection so every statement the executor makes in it rides ONE physical connection whose
    /// server backend the test can name: EF leaves a connection it did not open alone, so until that connection breaks
    /// it is the only one the executor's writes use.
    /// </summary>
    private static async Task<int> PinObserverConnectionAsync(ILifetimeScope scope)
    {
        var database = scope.Resolve<CodeSpaceDbContext>().Database;

        await database.OpenConnectionAsync();

        return ((NpgsqlConnection)database.GetDbConnection()).ProcessID;
    }

    /// <summary>Have the server end one backend — what an operator's <c>pg_terminate_backend</c>, a failover or an idle-session reaper does to a pooled connection — and wait until it is really gone.</summary>
    private async Task TerminateBackendAsync(int backendPid)
    {
        using (var scope = _fixture.BeginScope())
        {
            var terminated = await scope.Resolve<CodeSpaceDbContext>().Database.SqlQuery<bool>($"SELECT pg_terminate_backend({backendPid}) AS \"Value\"").SingleAsync();

            terminated.ShouldBeTrue($"the server must accept terminating the observer's backend {backendPid}");
        }

        await WaitUntilAsync(() => !BackendAlive(backendPid), TimeSpan.FromSeconds(10), $"backend {backendPid} was signalled but is still listed in pg_stat_activity");
    }

    private bool BackendAlive(int backendPid)
    {
        using var scope = _fixture.BeginScope();

        return scope.Resolve<CodeSpaceDbContext>().Database.SqlQuery<int>($"SELECT count(*)::integer AS \"Value\" FROM pg_stat_activity WHERE pid = {backendPid}").Single() > 0;
    }

    private List<string> EventTextsOf(Guid runId)
    {
        using var scope = _fixture.BeginScope();

        return scope.Resolve<CodeSpaceDbContext>().AgentRunEvent.AsNoTracking().Where(e => e.AgentRunId == runId).OrderBy(e => e.Sequence).Select(e => e.Text).ToList();
    }

    private CodeSpace.Core.Persistence.Entities.AgentRun RunOf(Guid runId)
    {
        using var scope = _fixture.BeginScope();

        return scope.Resolve<CodeSpaceDbContext>().AgentRun.AsNoTracking().Single(r => r.Id == runId);
    }

    /// <summary>Whether the fault came out of Npgsql itself, however EF wrapped it on the way — the independent check that a staged database fault really reached the write, without asking the classifier under test.</summary>
    private static bool RaisedByNpgsql(Exception fault)
    {
        for (var current = (Exception?)fault; current is not null; current = current.InnerException)
            if (current is NpgsqlException) return true;

        return false;
    }
}
