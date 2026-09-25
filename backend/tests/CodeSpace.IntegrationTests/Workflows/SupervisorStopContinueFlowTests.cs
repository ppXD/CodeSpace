using Autofac;
using CodeSpace.Core.Middlewares.Transactional;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Supervisor;
using CodeSpace.Core.Services.Workflows;
using CodeSpace.Core.Services.Workflows.Engine;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.IntegrationTests.Infrastructure.Jobs;
using CodeSpace.IntegrationTests.Workflows.Infrastructure;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Commands.Workflows;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Dtos.Workflows;
using CodeSpace.Messages.Enums;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace CodeSpace.IntegrationTests.Workflows;

/// <summary>
/// Stop then Continue while a supervisor wave is in flight. A stop reaches only a walk on the host that took it, and its
/// teardown runs after its commit, so a Continue can land while the old walk is still deciding its next wave, and before
/// the teardown has ended the stopped wave. The revived run must own neither. The old walk's wave is refused at its
/// staging, which takes the engine park's lock on the run at the generation that walk claimed; and the revive closes
/// what the stopped attempt left pending, so the revived turn stages a wave of its own and the late teardown finds
/// nothing of it to end.
///
/// <para>Fidelity 🟢 high: the REAL engine for every walk (the overtaken one on its own cancellation registry, as on
/// another host, so the stop never trips it), the REAL supervisor node, turn service and executor, the REAL cancel (its
/// flip, and its post-commit teardown held back the way a slow kill-wave holds it) and the REAL Continue, over real
/// Postgres. The scripted decider stands in for the model, and holds the overtaken turn mid-decision, the window a stop
/// and a continue most likely land in. The binary-less harness never runs (<c>ManualExecution()</c>). One case drives
/// the stopped turn through the turn service directly, because the supervisor node resolves it from the root container
/// where a test override cannot reach, with its decision's terminal write interrupted as the stop's tripped token
/// interrupts it.</para>
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public class SupervisorStopContinueFlowTests : IDisposable
{
    private const string Goal = "ship the feature";

    private static readonly TimeSpan SignalTimeout = TimeSpan.FromSeconds(30);

    private readonly PostgresFixture _fixture;

    public SupervisorStopContinueFlowTests(PostgresFixture fixture)
    {
        _fixture = fixture;

        using var scope = _fixture.BeginScope();
        scope.Resolve<SupervisorDecisionScript>().PlanThenSpawnForever();   // turn 0 plans, every later turn spawns both units
    }

    public void Dispose()
    {
        using var scope = _fixture.BeginScope();
        scope.Resolve<SupervisorDecisionScript>().PlanThenStop();   // restore the default for sibling tests
    }

    [Fact]
    public async Task A_turn_a_continue_overtook_while_it_decided_stages_nothing_and_the_revived_turn_stages_the_wave()
    {
        // The old walk's turn is mid-decision when the run is stopped and continued; it then claims its spawn and reaches
        // the staging with the run already revived. Its wave used to commit under the revived run, which then re-parked on
        // it as its own.
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var runId = await SeedSupervisorRunAsync(teamId, userId);

        using var manual = ResolveJobClient().ManualExecution();

        await RunEngineAsync(runId);   // turn 0 plans and parks on its self-advance
        await ResolveSelfAdvanceAsync(runId);

        var deciding = ResolveScript().HoldNextDecision(runId);
        var overtakenWalk = WalkOnAnotherHostInBackground(runId);
        await AwaitSignalAsync(deciding.Started.Task, "the old walk's turn 1 reaching its decision");

        await StopAsync(runId, teamId);
        await ContinueAsync(runId, teamId);

        deciding.Release.TrySetResult();
        var overtaken = await Record.ExceptionAsync(() => AwaitSignalAsync(overtakenWalk, "the overtaken walk returning"));

        overtaken.ShouldBeNull("the overtaken walk stood down at its staging, writing nothing");
        (await AgentIdsAsync(runId)).ShouldBeEmpty("the overtaken turn staged no agent under the revived run");
        (await AgentWaitsAsync(runId)).ShouldBeEmpty("nor any agent wait");
        (await NodeFailuresAsync(runId, "sup")).ShouldBe(0, "standing down is not recorded as the supervisor step failing");

        await RunEngineAsync(runId);   // the revived walk re-runs the spawn the overtaken turn claimed

        var wave = await AgentWaitsAsync(runId);
        wave.Select(w => w.IterationKey).ShouldBe(new[] { "sup#turn1#0", "sup#turn1#1" }, ignoreOrder: true, "the revived turn staged the wave itself");
        wave.ShouldAllBe(w => w.Status == WorkflowWaitStatuses.Pending, "and parked on it");

        var waveAgents = wave.Select(w => Guid.Parse(w.Token)).ToList();
        (await AgentIdsAsync(runId)).ShouldBe(waveAgents, ignoreOrder: true, "the only agents under the run are the revived wave's");
        (await AgentStatusesAsync(waveAgents)).ShouldAllBe(s => s == AgentRunStatus.Queued, "queued for their own execution");
        SupervisorOutcome.ReadStagedAgentRunIds((await SpawnDecisionAsync(runId, teamId)).OutcomeJson).ShouldBe(waveAgents, ignoreOrder: true, "the spawn decision records the wave the revived turn staged");
        (await RunStatusAsync(runId)).ShouldBe(WorkflowRunStatus.Suspended, "the revived run is parked on its wave");
    }

    [Fact]
    public async Task A_stop_teardown_landing_after_a_continue_finds_nothing_of_the_revived_wave_to_end()
    {
        // Stop during a wave (one agent claimed by a worker, one still queued), then a Continue before the stop's teardown
        // ran. The revived supervisor re-parked on the stopped wave's open waits, or reclaimed its queued agent for its own
        // next wave; the teardown then closed those waits and ended those agents, and the revived run sat parked on
        // nothing until the reconciler re-dispatched it.
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var runId = await SeedSupervisorRunAsync(teamId, userId);

        using var manual = ResolveJobClient().ManualExecution();

        await RunEngineAsync(runId);   // turn 0 plans
        await ResolveSelfAdvanceAsync(runId);
        await RunEngineAsync(runId);   // turn 1 spawns both units and parks on them

        var stoppedWave = await AgentWaitsAsync(runId);
        stoppedWave.Count.ShouldBe(2, "precondition: turn 1 parked on a two-agent wave");
        var claimedAgent = Guid.Parse(stoppedWave.Single(w => w.IterationKey == "sup#turn1#0").Token);
        var queuedAgent = Guid.Parse(stoppedWave.Single(w => w.IterationKey == "sup#turn1#1").Token);
        await MarkRunningAsync(claimedAgent);   // a worker claimed one of them

        using var stop = await WorkflowsTestSeed.StopWithTeardownHeldAsync(_fixture, runId, teamId);
        await ContinueAsync(runId, teamId);

        (await WaitStatusesAsync(stoppedWave)).ShouldAllBe(s => s == WorkflowWaitStatuses.Discarded, "the revive closed the stopped wave's waits, so nothing re-parks on them");
        (await AgentStatusesAsync(new[] { queuedAgent })).ShouldBe(new[] { AgentRunStatus.Cancelled }, "the revive cancelled the stopped wave's queued agent, so no reclaim can take it");
        (await AgentStatusesAsync(new[] { claimedAgent })).ShouldBe(new[] { AgentRunStatus.Running }, "a running agent is left to the stop's own teardown to kill — the revive does not wait on a kill");

        await RunEngineAsync(runId);   // the revived walk: turn 2 spawns its own wave
        await stop.Resolve<IPostCommitActions>().RunAllAsync(CancellationToken.None);   // the stop's teardown lands only now

        await AssertParkedOnARevivedWaveAsync(runId, "sup#turn2#", claimedAgent, queuedAgent);
        (await AgentStatusesAsync(new[] { claimedAgent })).ShouldBe(new[] { AgentRunStatus.Cancelled }, "the late teardown still killed the stopped wave's running agent");
    }

    [Fact]
    public async Task A_stopped_wave_whose_decision_was_still_in_flight_is_staged_afresh_rather_than_re_parked_on()
    {
        // The stop landed between the wave's commit and its spawn decision's terminal record, so the Continue replays that
        // decision. The replay found the wave's rows — closed by the stop — and re-parked on them as a wave already
        // staged, where nothing would ever answer.
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var runId = await SeedSupervisorRunAsync(teamId, userId);

        using var manual = ResolveJobClient().ManualExecution();

        await RunEngineAsync(runId);   // turn 0 plans
        await ResolveSelfAdvanceAsync(runId);
        await StageWaveBehindAnInterruptedDecisionAsync(runId, teamId);

        var stoppedWave = await AgentWaitsAsync(runId);
        stoppedWave.Count.ShouldBe(2, "precondition: turn 1 committed its two-agent wave");
        var stoppedAgents = stoppedWave.Select(w => Guid.Parse(w.Token)).ToArray();
        (await SpawnDecisionAsync(runId, teamId)).Status.ShouldBe(SupervisorDecisionStatus.Running, "precondition: the spawn decision was still in flight");

        using var stop = await WorkflowsTestSeed.StopWithTeardownHeldAsync(_fixture, runId, teamId);
        await ContinueAsync(runId, teamId);

        (await AgentStatusesAsync(stoppedAgents)).ShouldAllBe(s => s == AgentRunStatus.Cancelled, "the revive cancelled the stopped wave's agents — never dispatched, both still queued");

        await RunEngineAsync(runId);   // the revived walk replays the in-flight spawn
        await stop.Resolve<IPostCommitActions>().RunAllAsync(CancellationToken.None);

        await AssertParkedOnARevivedWaveAsync(runId, "sup#turn1#", stoppedAgents);
        (await SpawnDecisionAsync(runId, teamId)).Status.ShouldBe(SupervisorDecisionStatus.Succeeded, "the replay finished the spawn decision on its fresh wave");
    }

    /// <summary>After the late teardown: the revived run is parked on exactly one two-agent wave under <paramref name="keyPrefix"/>, none of it the stopped attempt's, all of it still open and queued.</summary>
    private async Task AssertParkedOnARevivedWaveAsync(Guid runId, string keyPrefix, params Guid[] stoppedAgents)
    {
        var pending = (await AgentWaitsAsync(runId)).Where(w => w.Status == WorkflowWaitStatuses.Pending).ToList();
        pending.Select(w => w.IterationKey).ShouldBe(new[] { keyPrefix + "0", keyPrefix + "1" }, ignoreOrder: true, "the revived run is parked on a wave of its own, and the late teardown left it open");

        var revivedAgents = pending.Select(w => Guid.Parse(w.Token)).ToList();
        revivedAgents.ShouldNotContain(id => stoppedAgents.Contains(id), "the revived wave reuses none of the stopped attempt's agents");
        (await AgentStatusesAsync(revivedAgents)).ShouldAllBe(s => s == AgentRunStatus.Queued, "the late teardown ended none of the revived wave's agents");
        (await RunStatusAsync(runId)).ShouldBe(WorkflowRunStatus.Suspended, "the revived run is parked on its wave");
    }

    /// <summary>Turn 1 of the stopped attempt stages its wave, and the stop interrupts it before it records its spawn decision: the wave committed, the decision still in flight.</summary>
    private async Task StageWaveBehindAnInterruptedDecisionAsync(Guid runId, Guid teamId)
    {
        using var scope = _fixture.BeginScope(b => b.RegisterDecorator<ISupervisorDecisionLog>((_, _, inner) => new InterruptedTerminalDecisionLog(inner)));

        var interrupted = await Record.ExceptionAsync(() => scope.Resolve<ISupervisorTurnService>().RunTurnAsync(runId, teamId, "sup", Goal, conversationId: null, goalConfig: null, CancellationToken.None));

        interrupted.ShouldBeOfType<OperationCanceledException>("precondition: the turn staged its wave, then its terminal write was interrupted");
    }

    /// <summary>Awaits <paramref name="signal"/> for at most <see cref="SignalTimeout"/>, failing with the signal's name.</summary>
    private static async Task AwaitSignalAsync(Task signal, string name)
    {
        try { await signal.WaitAsync(SignalTimeout); }
        catch (TimeoutException) { throw new TimeoutException($"Timed out after {SignalTimeout.TotalSeconds}s waiting for {name}."); }
    }

    private async Task<Guid> SeedSupervisorRunAsync(Guid teamId, Guid userId)
    {
        Guid workflowId;
        using (var scope = _fixture.BeginScopeAs(userId, teamId, Roles.Admin))
            workflowId = await scope.Resolve<IMediator>().Send(new CreateWorkflowCommand
            {
                Name = "sup-stop-continue-" + Guid.NewGuid().ToString("N")[..6],
                Description = null,
                Definition = SupervisorDefinition(),
                Activations = new List<WorkflowActivationInput>(),
                Enabled = true,
            });

        return await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, teamId);
    }

    private async Task RunEngineAsync(Guid runId)
    {
        using var scope = _fixture.BeginScope();
        await scope.Resolve<IWorkflowEngine>().ExecuteRunAsync(runId, CancellationToken.None);
    }

    /// <summary>A walk on another replica: its own in-process cancellation registry, which a stop landing on this host never trips.</summary>
    private Task WalkOnAnotherHostInBackground(Guid runId) => Task.Run(async () =>
    {
        using var scope = _fixture.BeginScope(b => b.RegisterType<RunCancellationRegistry>().As<IRunCancellationRegistry>().SingleInstance());
        await scope.Resolve<IWorkflowEngine>().ExecuteRunAsync(runId, CancellationToken.None);
    });

    /// <summary>Resolve the run's pending self-advance wait through the entry point the engine enqueues, so the next walk runs the next turn.</summary>
    private async Task ResolveSelfAdvanceAsync(Guid runId)
    {
        Guid waitId;
        using (var verify = _fixture.BeginScope())
            waitId = (await verify.Resolve<CodeSpaceDbContext>().WorkflowRunWait.AsNoTracking().SingleAsync(w => w.RunId == runId && w.WaitKind == WorkflowWaitKinds.SupervisorDecision && w.Status == WorkflowWaitStatuses.Pending)).Id;

        using var scope = _fixture.BeginScope();
        await scope.Resolve<IWorkflowResumeService>().ResumeWaitAsync(runId, waitId, null, CancellationToken.None);
    }

    private async Task StopAsync(Guid runId, Guid teamId)
    {
        using var scope = _fixture.BeginScope();
        (await scope.Resolve<IWorkflowService>().CancelRunAsync(runId, teamId, CancellationToken.None))!.Cancelled.ShouldBeTrue("the run was stopped");
    }

    private async Task ContinueAsync(Guid runId, Guid teamId)
    {
        using var scope = _fixture.BeginScope();
        (await scope.Resolve<IWorkflowService>().ContinueRunAsync(runId, teamId, CancellationToken.None)).ShouldBeTrue("the stopped run continues in place");
    }

    private async Task MarkRunningAsync(Guid agentRunId)
    {
        using var scope = _fixture.BeginScope();
        await scope.Resolve<IAgentRunService>().MarkRunningAsync(agentRunId, CancellationToken.None);
    }

    private async Task<IReadOnlyList<WorkflowRunWait>> AgentWaitsAsync(Guid runId)
    {
        using var scope = _fixture.BeginScope();
        return await scope.Resolve<CodeSpaceDbContext>().WorkflowRunWait.AsNoTracking().Where(w => w.RunId == runId && w.WaitKind == WorkflowWaitKinds.AgentRun).ToListAsync();
    }

    private async Task<IReadOnlyList<string>> WaitStatusesAsync(IEnumerable<WorkflowRunWait> waits)
    {
        var ids = waits.Select(w => w.Id).ToList();

        using var scope = _fixture.BeginScope();
        return await scope.Resolve<CodeSpaceDbContext>().WorkflowRunWait.AsNoTracking().Where(w => ids.Contains(w.Id)).Select(w => w.Status).ToListAsync();
    }

    private async Task<IReadOnlyList<Guid>> AgentIdsAsync(Guid runId)
    {
        using var scope = _fixture.BeginScope();
        return await scope.Resolve<CodeSpaceDbContext>().AgentRun.AsNoTracking().Where(a => a.WorkflowRunId == runId).Select(a => a.Id).ToListAsync();
    }

    private async Task<IReadOnlyList<AgentRunStatus>> AgentStatusesAsync(IReadOnlyCollection<Guid> agentRunIds)
    {
        using var scope = _fixture.BeginScope();
        return await scope.Resolve<CodeSpaceDbContext>().AgentRun.AsNoTracking().Where(a => agentRunIds.Contains(a.Id)).Select(a => a.Status).ToListAsync();
    }

    private async Task<int> NodeFailuresAsync(Guid runId, string nodeId)
    {
        using var scope = _fixture.BeginScope();
        return await scope.Resolve<CodeSpaceDbContext>().WorkflowRunRecord.AsNoTracking().CountAsync(r => r.RunId == runId && r.NodeId == nodeId && (r.RecordType == WorkflowRunRecordTypes.NodeFailed || r.RecordType == WorkflowRunRecordTypes.AttemptFailed));
    }

    private async Task<SupervisorDecisionRecord> SpawnDecisionAsync(Guid runId, Guid teamId)
    {
        using var scope = _fixture.BeginScope();
        return await scope.Resolve<CodeSpaceDbContext>().SupervisorDecisionRecord.AsNoTracking().SingleAsync(d => d.SupervisorRunId == runId && d.TeamId == teamId && d.DecisionKind == SupervisorDecisionKinds.Spawn);
    }

    private async Task<WorkflowRunStatus> RunStatusAsync(Guid runId)
    {
        using var scope = _fixture.BeginScope();
        return await scope.Resolve<CodeSpaceDbContext>().WorkflowRun.AsNoTracking().Where(r => r.Id == runId).Select(r => r.Status).SingleAsync();
    }

    private SupervisorDecisionScript ResolveScript()
    {
        using var scope = _fixture.BeginScope();
        return scope.Resolve<SupervisorDecisionScript>();
    }

    private InMemoryBackgroundJobClient ResolveJobClient()
    {
        using var scope = _fixture.BeginScope();
        return scope.Resolve<InMemoryBackgroundJobClient>();
    }

    // manual → sup (agent.supervisor) → terminal. Shadow completion: the simulated agents mint no delivery evidence, and
    // completion arbitration is not this suite's subject (mirrors SupervisorSpawnFlowTests).
    private static WorkflowDefinition SupervisorDefinition() => new()
    {
        SchemaVersion = 1,
        CompletionMode = WorkflowDefinition.CompletionModeShadow,
        Nodes = new List<NodeDefinition>
        {
            new() { Id = "start", TypeKey = "trigger.manual", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
            new() { Id = "sup", TypeKey = "agent.supervisor", Config = WorkflowsTestSeed.Json($$"""{"goal":"{{Goal}}"}"""), Inputs = WorkflowsTestSeed.EmptyJson() },
            new() { Id = "end", TypeKey = "builtin.terminal", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
        },
        Edges = new List<EdgeDefinition>
        {
            new() { From = "start", To = "sup" },
            new() { From = "sup", To = "end" },
        },
    };

    /// <summary>The decision log with its first terminal write interrupted the way a stop's tripped token interrupts it: the wave the executor just committed stays behind a decision still in flight. Every other call, and every later terminal write, reaches the real log.</summary>
    private sealed class InterruptedTerminalDecisionLog : ISupervisorDecisionLog
    {
        private readonly ISupervisorDecisionLog _inner;
        private int _interrupted;

        public InterruptedTerminalDecisionLog(ISupervisorDecisionLog inner) { _inner = inner; }

        public Task RecordTerminalAsync(Guid decisionId, Guid teamId, SupervisorDecisionStatus status, string? outcomeJson, string? error, CancellationToken cancellationToken) =>
            Interlocked.Exchange(ref _interrupted, 1) == 0
                ? throw new OperationCanceledException("The stop tripped the walk's token before it recorded the decision.")
                : _inner.RecordTerminalAsync(decisionId, teamId, status, outcomeJson, error, cancellationToken);

        public Task<SupervisorDecisionClaim> TryClaimAsync(SupervisorDecisionClaimRequest request, CancellationToken cancellationToken) => _inner.TryClaimAsync(request, cancellationToken);

        public Task<bool> TryBeginExecutionAsync(Guid decisionId, Guid teamId, CancellationToken cancellationToken) => _inner.TryBeginExecutionAsync(decisionId, teamId, cancellationToken);

        public Task<IReadOnlyList<SupervisorDecisionRecord>> GetForRunAsync(Guid supervisorRunId, Guid teamId, CancellationToken cancellationToken) => _inner.GetForRunAsync(supervisorRunId, teamId, cancellationToken);

        public Task<IReadOnlyList<SupervisorPriorDecision>> GetTerminalDecisionsAsync(Guid supervisorRunId, Guid teamId, CancellationToken cancellationToken) => _inner.GetTerminalDecisionsAsync(supervisorRunId, teamId, cancellationToken);

        public Task UpdateOutcomeAsync(Guid decisionId, Guid teamId, string foldedOutcomeJson, CancellationToken cancellationToken) => _inner.UpdateOutcomeAsync(decisionId, teamId, foldedOutcomeJson, cancellationToken);

        public Task<int> ExpireStalePendingAsync(DateTimeOffset olderThan, CancellationToken cancellationToken) => _inner.ExpireStalePendingAsync(olderThan, cancellationToken);
    }
}
