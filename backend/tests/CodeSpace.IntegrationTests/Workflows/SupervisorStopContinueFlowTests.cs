using System.Text.Json;
using Autofac;
using CodeSpace.Core.Middlewares.Transactional;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Supervisor;
using CodeSpace.Core.Services.Workflows;
using CodeSpace.Core.Services.Workflows.Engine;
using CodeSpace.Core.Services.Workflows.Nodes.Builtin;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.IntegrationTests.Infrastructure.Jobs;
using CodeSpace.IntegrationTests.Workflows.Infrastructure;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Commands.Workflows;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Decisions;
using CodeSpace.Messages.Dtos.Agents;
using CodeSpace.Messages.Dtos.Workflows;
using CodeSpace.Messages.Enums;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Shouldly;

namespace CodeSpace.IntegrationTests.Workflows;

/// <summary>
/// Stop then Continue while a supervisor wave is in flight. A stop reaches only a walk on the host that took it, and its
/// teardown runs after its commit, so a Continue can land while the old walk is still deciding, claiming, staging or
/// recording its next wave, and before the teardown has ended the stopped wave. The revived run must own none of it. The
/// old walk's every write — its decision's claim, begin and terminal record, and its wave — takes the engine park's share
/// lock on the run at the generation that walk claimed, so it stands down the moment the revive has moved the run on; and
/// the revive ends what the stopped attempt left — its waits, its queued and running agents — so the revived turn folds
/// only finished work, stages a wave of its own, and the late teardown finds nothing of it to end.
///
/// <para>Every case runs capped and uncapped: under a cost cap the wave's budget admission rides in the same fenced
/// transaction, so an overtaken turn reserves nothing, and a closed wave staged afresh keeps the reservations its slots
/// were first admitted under instead of being refused as a different intent.</para>
///
/// <para>Fidelity 🟢 high: the REAL engine for every walk (the overtaken one on its own cancellation registry, as on
/// another host, so the stop never trips it), the REAL supervisor node, turn service, executor and budget ledger, the REAL
/// cancel (its flip, and its post-commit teardown held back the way a slow kill-wave holds it) and the REAL Continue, over
/// real Postgres. The scripted decider stands in for the model, and holds the overtaken turn mid-decision, the window a
/// stop and a continue most likely land in. The binary-less harness never runs (<c>ManualExecution()</c>). The cases that
/// hold an overtaken turn past its decision drive that turn through the turn service directly, under the walk's claim on
/// the run's generation, because the supervisor node resolves its own scope from the root container where a test's
/// decision-log hold or command interceptor cannot reach.</para>
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public class SupervisorStopContinueFlowTests : IDisposable
{
    private const string Goal = "ship the feature";

    private const decimal CapUsd = 10m;

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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_turn_a_continue_overtook_while_it_decided_stages_nothing_and_the_revived_turn_stages_the_wave(bool capped)
    {
        // The old walk's turn is mid-decision when the run is stopped and continued, and decides only after the revive. Its
        // decision used to be claimed under the revived run, which then replayed it as its own — and, before that, its wave
        // committed there too.
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var runId = await SeedSupervisorRunAsync(teamId, userId, capped);

        using var manual = ResolveJobClient().ManualExecution();

        await RunEngineAsync(runId);   // turn 0 plans and parks on its self-advance
        await ResolveSelfAdvanceAsync(runId);

        var deciding = ResolveScript().HoldNextDecision(runId);
        var overtakenWalk = WalkOnAnotherHostInBackground(runId);
        await StopContinueSignals.AwaitAsync(deciding.Started.Task, "the old walk's turn 1 reaching its decision");

        await StopAsync(runId, teamId);
        await ContinueAsync(runId, teamId);

        deciding.Release.TrySetResult();
        var overtaken = await Record.ExceptionAsync(() => StopContinueSignals.AwaitAsync(overtakenWalk, "the overtaken walk returning"));

        overtaken.ShouldBeNull("the overtaken walk stood down at its claim, writing nothing");
        (await DecisionKindsAsync(runId, teamId)).ShouldBe(new[] { SupervisorDecisionKinds.Plan }, "the overtaken turn claimed no decision under the revived run");
        (await AgentIdsAsync(runId)).ShouldBeEmpty("staged no agent");
        (await AgentWaitsAsync(runId)).ShouldBeEmpty("nor any agent wait");
        (await ReservationKeysAsync(runId)).ShouldBeEmpty("nor reserved any budget");
        (await NodeFailuresAsync(runId, "sup")).ShouldBe(0, "standing down is not recorded as the supervisor step failing");

        await RunEngineAsync(runId);   // the revived walk decides turn 1 itself

        var wave = await AgentWaitsAsync(runId);
        wave.Select(w => w.IterationKey).ShouldBe(WaveKeys(1), ignoreOrder: true, "the revived turn staged the wave itself");
        wave.ShouldAllBe(w => w.Status == WorkflowWaitStatuses.Pending, "and parked on it");

        var waveAgents = wave.Select(w => Guid.Parse(w.Token)).ToList();
        (await AgentIdsAsync(runId)).ShouldBe(waveAgents, ignoreOrder: true, "the only agents under the run are the revived wave's");
        (await AgentStatusesAsync(waveAgents)).ShouldBe(new[] { AgentRunStatus.Queued, AgentRunStatus.Queued }, "queued for their own execution");
        SupervisorOutcome.ReadStagedAgentRunIds((await SpawnDecisionAsync(runId, teamId)).OutcomeJson).ShouldBe(waveAgents, ignoreOrder: true, "the spawn decision records the wave the revived turn staged");
        (await ReservationKeysAsync(runId)).ShouldBe(capped ? WaveKeys(1) : Array.Empty<string>(), ignoreOrder: true, "a capped run holds one reservation per unit of the revived wave, and nothing more");
        (await RunStatusAsync(runId)).ShouldBe(WorkflowRunStatus.Suspended, "the revived run is parked on its wave");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_turn_a_continue_overtook_claims_nothing_after_the_revived_turn_decided_otherwise(bool capped)
    {
        // The revived walk decides turn 1 first, and only then does the overtaken turn come back from its decision — with a
        // different wave, as a model asked twice may. Its decision carries a key of its own, so nothing but the fence stands
        // in its way: it used to land as a second turn-1 decision, left in flight for the revived run to replay at its next
        // turn as a duplicate spawn.
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var runId = await SeedSupervisorRunAsync(teamId, userId, capped);

        using var manual = ResolveJobClient().ManualExecution();

        await RunEngineAsync(runId);   // turn 0 plans
        await ResolveSelfAdvanceAsync(runId);

        var deciding = ResolveScript().HoldNextDecision(runId, ScriptedSupervisorDecider.SpawnOf(ScriptedSupervisorDecider.SubtaskA));
        var overtakenWalk = WalkOnAnotherHostInBackground(runId);
        await StopContinueSignals.AwaitAsync(deciding.Started.Task, "the old walk's turn 1 reaching its decision");

        await StopAsync(runId, teamId);
        await ContinueAsync(runId, teamId);
        await RunEngineAsync(runId);   // the revived walk decides turn 1 — both units — stages them and parks

        var revivedWave = await AgentWaitsAsync(runId);
        revivedWave.Count.ShouldBe(2, "precondition: the revived turn staged its two-agent wave");
        var revivedSpawn = await SpawnDecisionAsync(runId, teamId);

        deciding.Release.TrySetResult();   // the overtaken turn now decides a one-unit wave and claims it
        (await Record.ExceptionAsync(() => StopContinueSignals.AwaitAsync(overtakenWalk, "the overtaken walk returning"))).ShouldBeNull("the overtaken walk stood down at its claim");

        var decisions = await DecisionsAsync(runId, teamId);
        decisions.Select(d => d.Id).ShouldBe(new[] { decisions[0].Id, revivedSpawn.Id }, "the overtaken decision was never inserted: the tape holds the plan and the revived turn's spawn, nothing else");
        decisions.ShouldAllBe(d => SupervisorDecisionStateMachine.IsTerminal(d.Status), "and no decision is left in flight for a later turn to replay");
        (await NodeFailuresAsync(runId, "sup")).ShouldBe(0, "standing down is not recorded as the supervisor step failing");

        (await WaitStatusesAsync(revivedWave)).ShouldBe(new[] { WorkflowWaitStatuses.Pending, WorkflowWaitStatuses.Pending }, "the revived wave is untouched");
        (await AgentIdsAsync(runId)).Count.ShouldBe(2, "and no agent exists beyond it");
        (await ReservationKeysAsync(runId)).ShouldBe(capped ? WaveKeys(1) : Array.Empty<string>(), ignoreOrder: true, "a capped run holds the revived wave's reservations only");
        (await RunStatusAsync(runId)).ShouldBe(WorkflowRunStatus.Suspended, "the revived run is parked on its wave");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_stop_teardown_landing_after_a_continue_finds_nothing_of_the_revived_wave_to_end(bool capped)
    {
        // Stop during a wave (one agent claimed by a worker, one still queued), then a Continue before the stop's teardown
        // ran. The revived supervisor re-parked on the stopped wave's open waits, or reclaimed its queued agent for its own
        // next wave; the teardown then closed those waits and ended those agents, and the revived run sat parked on nothing
        // until the reconciler re-dispatched it. And the running agent, still running when the revived turn folded its wave,
        // stood on the tape as "Running" for good.
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var runId = await SeedSupervisorRunAsync(teamId, userId, capped);

        using var manual = ResolveJobClient().ManualExecution();

        await RunEngineAsync(runId);   // turn 0 plans
        await ResolveSelfAdvanceAsync(runId);
        await RunEngineAsync(runId);   // turn 1 spawns both units and parks on them

        var stoppedWave = await AgentWaitsAsync(runId);
        stoppedWave.Count.ShouldBe(2, "precondition: turn 1 parked on a two-agent wave");
        var claimedAgent = Guid.Parse(stoppedWave.Single(w => w.IterationKey == "sup#turn1#0").Token);
        var queuedAgent = Guid.Parse(stoppedWave.Single(w => w.IterationKey == "sup#turn1#1").Token);
        await MarkRunningAsync(claimedAgent);   // a worker claimed one of them
        var question = await SeedOpenAgentDecisionAsync(teamId, claimedAgent);   // and it has a question out

        using var stop = await WorkflowsTestSeed.StopWithTeardownHeldAsync(_fixture, runId, teamId);
        await ContinueAsync(runId, teamId);

        var closedWaits = await WaitStatusesAsync(stoppedWave);
        closedWaits.Count.ShouldBe(2, "the stopped wave's waits are still there");
        closedWaits.ShouldAllBe(s => s == WorkflowWaitStatuses.Discarded, "the revive closed them, so nothing re-parks on them");
        (await AgentStatusesAsync(new[] { claimedAgent, queuedAgent })).ShouldBe(new[] { AgentRunStatus.Cancelled, AgentRunStatus.Cancelled }, "the revive ended both — the queued one, so no reclaim can take it, and the running one, so no revived turn folds it as still running");
        (await DecisionStatusAsync(question)).ShouldBe(ToolCallLedgerStatus.Expired, "the running agent's cancel finished once the revive committed: its open question left the queue with it");

        await RunEngineAsync(runId);   // the revived walk: turn 2 folds turn 1's wave and spawns its own
        await stop.Resolve<IPostCommitActions>().RunAllAsync(CancellationToken.None);   // the stop's teardown lands only now

        await AssertParkedOnARevivedWaveAsync(runId, "sup#turn2#", claimedAgent, queuedAgent);

        var folded = SupervisorOutcome.ReadAgentResults((await SpawnDecisionsAsync(runId, teamId))[0].OutcomeJson);
        folded.Count.ShouldBe(2, "turn 1's spawn folded a result for each of its agents");
        folded.ShouldAllBe(r => r.Status == nameof(AgentRunStatus.Cancelled), "each as it ended — never as still running");
        (await AgentStatusesAsync(new[] { claimedAgent, queuedAgent })).ShouldBe(new[] { AgentRunStatus.Cancelled, AgentRunStatus.Cancelled }, "the late teardown's CAS on what the revive already ended simply lost");
        (await ReservationKeysAsync(runId)).ShouldBe(capped ? WaveKeys(1).Concat(WaveKeys(2)).ToArray() : Array.Empty<string>(), ignoreOrder: true, "a capped run admitted the revived wave on reservations of its own");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_stopped_wave_whose_decision_was_still_in_flight_is_staged_afresh_rather_than_re_parked_on(bool capped)
    {
        // The stop landed between the wave's commit and its spawn decision's terminal record, so the Continue replays that
        // decision. The replay found the wave's rows — closed by the stop — and re-parked on them as a wave already
        // staged, where nothing would ever answer. Under a cost cap the fresh staging was refused as over budget: its
        // slots' reservations exist, and a re-reservation computes a new deadline, which the ledger reads as a new intent.
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var runId = await SeedSupervisorRunAsync(teamId, userId, capped);

        using var manual = ResolveJobClient().ManualExecution();

        await RunEngineAsync(runId);   // turn 0 plans
        await ResolveSelfAdvanceAsync(runId);
        await StageWaveBehindAnInterruptedDecisionAsync(runId, teamId, capped);

        var stoppedWave = await AgentWaitsAsync(runId);
        stoppedWave.Count.ShouldBe(2, "precondition: turn 1 committed its two-agent wave");
        var stoppedAgents = stoppedWave.Select(w => Guid.Parse(w.Token)).ToArray();
        (await SpawnDecisionAsync(runId, teamId)).Status.ShouldBe(SupervisorDecisionStatus.Running, "precondition: the spawn decision was still in flight");

        using var stop = await WorkflowsTestSeed.StopWithTeardownHeldAsync(_fixture, runId, teamId);
        await ContinueAsync(runId, teamId);

        (await AgentStatusesAsync(stoppedAgents)).ShouldBe(new[] { AgentRunStatus.Cancelled, AgentRunStatus.Cancelled }, "the revive cancelled the stopped wave's agents — never dispatched, both still queued");

        await RunEngineAsync(runId);   // the revived walk replays the in-flight spawn
        await stop.Resolve<IPostCommitActions>().RunAllAsync(CancellationToken.None);

        await AssertParkedOnARevivedWaveAsync(runId, "sup#turn1#", stoppedAgents);
        (await SpawnDecisionAsync(runId, teamId)).Status.ShouldBe(SupervisorDecisionStatus.Succeeded, "the replay finished the spawn decision on its fresh wave");
        (await ReservationKeysAsync(runId)).ShouldBe(capped ? WaveKeys(1) : Array.Empty<string>(), ignoreOrder: true, "the closed slots kept the reservations they were first admitted under — the fresh wave is neither refused nor reserved twice");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_stopped_wave_staged_afresh_keeps_the_agent_that_had_already_finished(bool capped)
    {
        // One agent of the stopped wave had finished and answered its wait before the stop. The fresh staging of the closed
        // wave dropped that answered wait with the closed one and ran the finished unit a second time.
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var runId = await SeedSupervisorRunAsync(teamId, userId, capped);

        using var manual = ResolveJobClient().ManualExecution();

        await RunEngineAsync(runId);   // turn 0 plans
        await ResolveSelfAdvanceAsync(runId);
        await StageWaveBehindAnInterruptedDecisionAsync(runId, teamId, capped);

        var stoppedWave = await AgentWaitsAsync(runId);
        stoppedWave.Count.ShouldBe(2, "precondition: turn 1 committed its two-agent wave");
        var finishedWait = stoppedWave.Single(w => w.IterationKey == "sup#turn1#0");
        var finishedAgent = Guid.Parse(finishedWait.Token);
        var closedAgent = Guid.Parse(stoppedWave.Single(w => w.IterationKey == "sup#turn1#1").Token);
        await SimulateAgentCompletionAsync(finishedAgent);
        (await WaitStatusesAsync(new[] { finishedWait })).ShouldBe(new[] { WorkflowWaitStatuses.Resolved }, "precondition: the first unit finished and its wait holds its answer");

        using var stop = await WorkflowsTestSeed.StopWithTeardownHeldAsync(_fixture, runId, teamId);
        await ContinueAsync(runId, teamId);
        await RunEngineAsync(runId);   // the revived walk replays the in-flight spawn
        await stop.Resolve<IPostCommitActions>().RunAllAsync(CancellationToken.None);

        var waits = await AgentWaitsAsync(runId);
        waits.Select(w => w.IterationKey).ShouldBe(WaveKeys(1), ignoreOrder: true, "one wait per slot of the wave");

        var kept = waits.Single(w => w.IterationKey == "sup#turn1#0");
        kept.Id.ShouldBe(finishedWait.Id, "the finished slot keeps its own answered wait");
        kept.Status.ShouldBe(WorkflowWaitStatuses.Resolved);

        var restaged = waits.Single(w => w.IterationKey == "sup#turn1#1");
        restaged.Status.ShouldBe(WorkflowWaitStatuses.Pending, "only the slot the stop closed was staged afresh");
        var freshAgent = Guid.Parse(restaged.Token);
        freshAgent.ShouldNotBe(closedAgent, "on an agent of its own");

        (await AgentStatusesAsync(new[] { finishedAgent })).ShouldBe(new[] { AgentRunStatus.Succeeded }, "the finished agent is left as it ended");
        (await AgentIdsAsync(runId)).ShouldBe(new[] { finishedAgent, closedAgent, freshAgent }, ignoreOrder: true, "and its unit is not attempted twice");
        SupervisorOutcome.ReadStagedAgentRunIds((await SpawnDecisionAsync(runId, teamId)).OutcomeJson).ShouldBe(new[] { finishedAgent, freshAgent }, "the decision records the finished agent in its own slot and the fresh one in the closed slot, in spawn order");
        (await ReservationKeysAsync(runId)).ShouldBe(capped ? WaveKeys(1) : Array.Empty<string>(), ignoreOrder: true, "each slot keeps the one reservation it was first admitted under");
        (await RunStatusAsync(runId)).ShouldBe(WorkflowRunStatus.Suspended, "the revived run is parked on the restaged slot");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_turn_that_claimed_its_decision_before_the_stop_begins_nothing_after_the_continue(bool capped)
    {
        // The overtaken turn's claim committed before the stop; its begin landed after the Continue and flipped the decision
        // Running under the revived run — the state a revived walk reads as a crashed walk's execution to recover.
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var runId = await SeedSupervisorRunAsync(teamId, userId, capped);

        using var manual = ResolveJobClient().ManualExecution();

        await RunEngineAsync(runId);   // turn 0 plans
        await ResolveSelfAdvanceAsync(runId);

        var claimed = new DecisionLogHold(DecisionLogStep.AfterClaim);
        var overtakenTurn = RunOvertakenTurnInBackground(runId, teamId, capped, claimed.Decorate);

        try
        {
            await StopContinueSignals.AwaitAsync(claimed.Reached.Task, "the overtaken turn's claim committing");

            await StopAsync(runId, teamId);
            await ContinueAsync(runId, teamId);
        }
        finally
        {
            claimed.Release.TrySetResult();
        }

        (await Record.ExceptionAsync(() => StopContinueSignals.AwaitAsync(overtakenTurn, "the overtaken turn returning"))).ShouldBeOfType<RunSupersededException>("the overtaken turn stood down at its begin");
        (await SpawnDecisionAsync(runId, teamId)).Status.ShouldBe(SupervisorDecisionStatus.Pending, "it began nothing: the decision it claimed before the stop waits, Pending, for the revived walk");
        (await AgentIdsAsync(runId)).ShouldBeEmpty("staged no agent");
        (await ReservationKeysAsync(runId)).ShouldBeEmpty("nor reserved any budget");

        await RunEngineAsync(runId);   // the revived walk finds the decision in flight and finishes it

        await AssertParkedOnARevivedWaveAsync(runId, "sup#turn1#");
        await AssertTheSpawnDecisionRecordsTheParkedWaveAsync(runId, teamId);
        (await ReservationKeysAsync(runId)).ShouldBe(capped ? WaveKeys(1) : Array.Empty<string>(), ignoreOrder: true, "a capped run holds the finished wave's reservations only");
        (await NodeFailuresAsync(runId, "sup")).ShouldBe(0);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_turn_already_assembling_its_wave_when_the_continue_lands_reserves_nothing(bool capped)
    {
        // The overtaken turn began its spawn before the stop and was still assembling its wave when the Continue landed. Its
        // budget admission ran ahead of the wave's fence: a capped run's reservations committed on their own, the fence then
        // refused the wave, and the revived walk's replay was refused as a different intent on those very slots — told its
        // spawn was over budget.
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var runId = await SeedSupervisorRunAsync(teamId, userId, capped);

        using var manual = ResolveJobClient().ManualExecution();

        await RunEngineAsync(runId);   // turn 0 plans
        await ResolveSelfAdvanceAsync(runId);

        var assembling = new HeldCommand(IsTurnWaveRead);   // past the turn's stand-down check, before its wave's transaction
        var overtakenTurn = RunOvertakenTurnInBackground(runId, teamId, capped, null, assembling);

        try
        {
            await StopContinueSignals.AwaitAsync(assembling.Reached.Task, "the overtaken turn reading its wave");

            await StopAsync(runId, teamId);
            await ContinueAsync(runId, teamId);
        }
        finally
        {
            assembling.Release.TrySetResult();
        }

        (await Record.ExceptionAsync(() => StopContinueSignals.AwaitAsync(overtakenTurn, "the overtaken turn returning"))).ShouldBeOfType<RunSupersededException>("the overtaken turn stood down at its wave's fence");
        (await ReservationKeysAsync(runId)).ShouldBeEmpty("it reserved no budget: admission runs behind the fence, in the wave's own transaction");
        (await AgentIdsAsync(runId)).ShouldBeEmpty("staged no agent");
        (await AgentWaitsAsync(runId)).ShouldBeEmpty("nor any wait");
        (await SpawnDecisionAsync(runId, teamId)).Status.ShouldBe(SupervisorDecisionStatus.Running, "the decision it began before the stop is still in flight");

        await RunEngineAsync(runId);   // the revived walk finds it in flight and finishes it

        await AssertParkedOnARevivedWaveAsync(runId, "sup#turn1#");
        await AssertTheSpawnDecisionRecordsTheParkedWaveAsync(runId, teamId);
        (await ReservationKeysAsync(runId)).ShouldBe(capped ? WaveKeys(1) : Array.Empty<string>(), ignoreOrder: true, "a capped run admitted the revived wave, one reservation per unit");
        (await NodeFailuresAsync(runId, "sup")).ShouldBe(0);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_turn_whose_wave_committed_before_the_stop_records_no_terminal_after_the_continue(bool capped)
    {
        // The overtaken turn committed its wave before the stop and wrote its spawn decision's terminal only after the
        // Continue. That terminal named the wave the revive had just closed, so the revived run moved on past it to a next
        // turn — or, had the revived walk replayed the decision first, one of the two lost the CAS and failed the step.
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var runId = await SeedSupervisorRunAsync(teamId, userId, capped);

        using var manual = ResolveJobClient().ManualExecution();

        await RunEngineAsync(runId);   // turn 0 plans
        await ResolveSelfAdvanceAsync(runId);

        var terminal = new DecisionLogHold(DecisionLogStep.BeforeTerminal);
        var overtakenTurn = RunOvertakenTurnInBackground(runId, teamId, capped, terminal.Decorate);
        IReadOnlyList<WorkflowRunWait> stoppedWave;
        ILifetimeScope stop;

        try
        {
            await StopContinueSignals.AwaitAsync(terminal.Reached.Task, "the overtaken turn's wave committing, its terminal still to write");

            stoppedWave = await AgentWaitsAsync(runId);
            stop = await WorkflowsTestSeed.StopWithTeardownHeldAsync(_fixture, runId, teamId);
            await ContinueAsync(runId, teamId);
        }
        finally
        {
            terminal.Release.TrySetResult();
        }

        using (stop)
        {
            stoppedWave.Count.ShouldBe(2, "precondition: the overtaken turn committed its two-agent wave before the stop");

            (await Record.ExceptionAsync(() => StopContinueSignals.AwaitAsync(overtakenTurn, "the overtaken turn returning"))).ShouldBeOfType<RunSupersededException>("the overtaken turn stood down at its terminal record");
            (await SpawnDecisionAsync(runId, teamId)).Status.ShouldBe(SupervisorDecisionStatus.Running, "it recorded nothing: the decision is still in flight, for the revived walk to finish");

            await RunEngineAsync(runId);   // the revived walk replays the decision: the wave it finds is closed
            await stop.Resolve<IPostCommitActions>().RunAllAsync(CancellationToken.None);
        }

        await AssertParkedOnARevivedWaveAsync(runId, "sup#turn1#", stoppedWave.Select(w => Guid.Parse(w.Token)).ToArray());
        await AssertTheSpawnDecisionRecordsTheParkedWaveAsync(runId, teamId);
        (await ReservationKeysAsync(runId)).ShouldBe(capped ? WaveKeys(1) : Array.Empty<string>(), ignoreOrder: true, "the restaged slots kept the overtaken wave's reservations");
        (await NodeFailuresAsync(runId, "sup")).ShouldBe(0, "neither walk recorded the supervisor step failing");
    }

    [Fact]
    public async Task A_stop_landing_while_a_wave_is_staged_waits_for_the_whole_wave_and_its_teardown_ends_it()
    {
        // The wave's fence is a share lock held to the wave's commit. A stop landing mid-staging waits for the whole wave,
        // so the agents it captures and ends are all of it; a check that held no lock let the stop commit first, and the
        // wave then committed under the stopped run behind its teardown's back.
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var runId = await SeedSupervisorRunAsync(teamId, userId, capped: false);

        using var manual = ResolveJobClient().ManualExecution();

        await RunEngineAsync(runId);   // turn 0 plans
        await ResolveSelfAdvanceAsync(runId);

        var staging = new HeldCommand(text => text.Contains("INSERT INTO workflow_run_wait"));   // inside the wave's transaction, its agents created
        var turn = RunOvertakenTurnInBackground(runId, teamId, false, null, staging);

        using var stop = _fixture.BeginScope();
        Task<CancelRunOutcome?> stopping;

        try
        {
            await StopContinueSignals.AwaitAsync(staging.Reached.Task, "the turn's wave, fenced and its agents created, staging its waits");

            var stopDb = stop.Resolve<CodeSpaceDbContext>();
            await stopDb.Database.OpenConnectionAsync();
            var stopPid = await stopDb.Database.SqlQueryRaw<int>("SELECT pg_backend_pid() AS \"Value\"").SingleAsync();

            stopping = StopHoldingTeardownAsync(stop, runId, teamId);
            await StopContinueSignals.WaitForLockWaitAsync(_fixture, stopPid, "the stop's flip to block on the wave's share lock on the run row", stopping);
        }
        finally
        {
            staging.Release.TrySetResult();
        }

        (await stopping)!.AgentRunsCancelled.ShouldBe(2, "the stop, let through once the wave committed, captured the whole of it");
        await StopContinueSignals.AwaitAsync(turn, "the turn finishing its wave");
        await stop.Resolve<IPostCommitActions>().RunAllAsync(CancellationToken.None);

        var wave = await AgentWaitsAsync(runId);
        wave.Select(w => w.IterationKey).ShouldBe(WaveKeys(1), ignoreOrder: true, "the wave the stop waited for");
        wave.ShouldAllBe(w => w.Status == WorkflowWaitStatuses.Discarded, "its waits closed by the teardown");
        (await AgentStatusesAsync(wave.Select(w => Guid.Parse(w.Token)).ToList())).ShouldBe(new[] { AgentRunStatus.Cancelled, AgentRunStatus.Cancelled }, "and its agents ended, none left queued under the stopped run");
    }

    [Fact]
    public async Task A_walk_whose_decision_the_revived_walk_finished_first_stands_down_without_failing_the_step()
    {
        // The old walk had begun turn 0's plan when the run was stopped — on a host the stop never reached — and continued.
        // The revived walk found the plan in flight, re-ran it and recorded it; only then did the old walk's executor
        // return. Its terminal record read the revived walk's Succeeded, threw it as an illegal Succeeded → Succeeded
        // ahead of any fence, and the engine recorded the supervisor step failed in the revived run's journal, where the
        // run's next walk read the step as failed.
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var runId = await SeedSupervisorRunAsync(teamId, userId, capped: false);

        using var manual = ResolveJobClient().ManualExecution();

        var recording = ResolveScript().HoldDecisionLog(runId, DecisionLogStep.BeforeTerminal);
        var overtakenWalk = WalkOnAnotherHostInBackground(runId);   // turn 0 claims, begins and executes its plan, then holds

        try
        {
            await StopContinueSignals.AwaitAsync(recording.Reached.Task, "the old walk's plan executed, its terminal still to write");

            await StopAsync(runId, teamId);
            await ContinueAsync(runId, teamId);
            await RunEngineAsync(runId);   // the revived walk finds the plan in flight, re-runs it and records it first

            (await DecisionsAsync(runId, teamId)).ShouldHaveSingleItem("precondition: the plan is the run's one decision").Status
                .ShouldBe(SupervisorDecisionStatus.Succeeded, "precondition: the revived walk recorded it first");
        }
        finally
        {
            recording.Release.TrySetResult();
        }

        (await Record.ExceptionAsync(() => StopContinueSignals.AwaitAsync(overtakenWalk, "the overtaken walk returning"))).ShouldBeNull("the overtaken walk stood down at its terminal record");
        (await NodeFailuresAsync(runId, "sup")).ShouldBe(0, "no attempt.failed or node.failed for the supervisor step landed in the revived run");
        (await RunStatusAsync(runId)).ShouldBe(WorkflowRunStatus.Suspended, "the revived run is parked on its self-advance, not failed");

        await ResolveSelfAdvanceAsync(runId);
        await RunEngineAsync(runId);   // the revived run's next walk

        (await AgentWaitsAsync(runId)).Select(w => w.IterationKey).ShouldBe(WaveKeys(1), ignoreOrder: true, "it ran the supervisor on to turn 1: nothing marked the step failed");
    }

    [Fact]
    public async Task A_continue_whose_request_went_away_after_its_commit_still_ends_the_running_agents_and_dispatches_the_run()
    {
        // The revive's post-commit half ran on the Continue request's own token. A client that went away after the commit
        // cancelled it mid-drain: the flipped agent kept its question open, its spend claims live and its harness execution
        // open, and the revived run sat undispatched until the reconciler's sweep.
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var runId = await SeedSupervisorRunAsync(teamId, userId, capped: false);

        using var manual = ResolveJobClient().ManualExecution();

        await RunEngineAsync(runId);   // turn 0 plans
        await ResolveSelfAdvanceAsync(runId);
        await RunEngineAsync(runId);   // turn 1 spawns both units and parks on them

        var claimedAgent = Guid.Parse((await AgentWaitsAsync(runId)).Single(w => w.IterationKey == "sup#turn1#0").Token);
        await MarkRunningAsync(claimedAgent);
        var question = await SeedOpenAgentDecisionAsync(teamId, claimedAgent);

        using var stop = await WorkflowsTestSeed.StopWithTeardownHeldAsync(_fixture, runId, teamId);   // its teardown still to come

        using var request = new CancellationTokenSource();
        using var command = _fixture.BeginScope();

        await using (var transaction = await command.Resolve<CodeSpaceDbContext>().Database.BeginTransactionAsync())
        {
            (await command.Resolve<IWorkflowService>().ContinueRunAsync(runId, teamId, request.Token)).ShouldBeTrue("the stopped run continues in place");
            await transaction.CommitAsync();
        }

        request.Cancel();   // the client went away once the Continue committed
        await command.Resolve<IPostCommitActions>().RunAllAsync(request.Token);

        (await AgentStatusesAsync(new[] { claimedAgent })).ShouldBe(new[] { AgentRunStatus.Cancelled }, "precondition: the revive flipped the running agent");
        (await DecisionStatusAsync(question)).ShouldBe(ToolCallLedgerStatus.Expired, "its cancel still finished: the agent's open question left the queue");
        (await RunStatusAsync(runId)).ShouldBe(WorkflowRunStatus.Enqueued, "and the revived run was still dispatched");
    }

    /// <summary>After the late teardown: the revived run is parked on exactly one two-agent wave under <paramref name="keyPrefix"/>, none of it the stopped attempt's, all of it still open and queued.</summary>
    private async Task AssertParkedOnARevivedWaveAsync(Guid runId, string keyPrefix, params Guid[] stoppedAgents)
    {
        var pending = (await AgentWaitsAsync(runId)).Where(w => w.Status == WorkflowWaitStatuses.Pending).ToList();
        pending.Select(w => w.IterationKey).ShouldBe(new[] { keyPrefix + "0", keyPrefix + "1" }, ignoreOrder: true, "the revived run is parked on a wave of its own, and the late teardown left it open");

        var revivedAgents = pending.Select(w => Guid.Parse(w.Token)).ToList();
        revivedAgents.ShouldNotContain(id => stoppedAgents.Contains(id), "the revived wave reuses none of the stopped attempt's agents");
        (await AgentStatusesAsync(revivedAgents)).ShouldBe(new[] { AgentRunStatus.Queued, AgentRunStatus.Queued }, "the late teardown ended none of the revived wave's agents");
        (await RunStatusAsync(runId)).ShouldBe(WorkflowRunStatus.Suspended, "the revived run is parked on its wave");
    }

    /// <summary>The run's one spawn decision is finished, and its tape names exactly the wave the run is parked on.</summary>
    private async Task AssertTheSpawnDecisionRecordsTheParkedWaveAsync(Guid runId, Guid teamId)
    {
        var parkedOn = (await AgentWaitsAsync(runId)).Where(w => w.Status == WorkflowWaitStatuses.Pending).Select(w => Guid.Parse(w.Token)).ToList();
        var spawn = await SpawnDecisionAsync(runId, teamId);

        spawn.Status.ShouldBe(SupervisorDecisionStatus.Succeeded, "the revived walk finished the decision itself");
        SupervisorOutcome.ReadStagedAgentRunIds(spawn.OutcomeJson).ShouldBe(parkedOn, ignoreOrder: true, "and the tape names the wave the run is parked on");
    }

    /// <summary>Turn 1 of the stopped attempt stages its wave, and the stop interrupts it before it records its spawn decision: the wave committed, the decision still in flight.</summary>
    private async Task StageWaveBehindAnInterruptedDecisionAsync(Guid runId, Guid teamId, bool capped)
    {
        using var scope = _fixture.BeginScope(b => b.RegisterDecorator<ISupervisorDecisionLog>((_, _, inner) => new InterruptedTerminalDecisionLog(inner)));

        var interrupted = await Record.ExceptionAsync(() => scope.Resolve<ISupervisorTurnService>().RunTurnAsync(runId, teamId, "sup", Goal, conversationId: null, GoalConfig(capped), CancellationToken.None));

        interrupted.ShouldBeOfType<OperationCanceledException>("precondition: the turn staged its wave, then its terminal write was interrupted");
    }

    /// <summary>The overtaken walk's next turn, on another host: the REAL turn service under that walk's claim on the run's current generation, in a scope with the test's hold registered and its commands through <paramref name="interceptors"/>.</summary>
    private Task RunOvertakenTurnInBackground(Guid runId, Guid teamId, bool capped, Action<ContainerBuilder>? hold, params IInterceptor[] interceptors) => Task.Run(async () =>
    {
        var generation = await GenerationAsync(runId);

        using var scope = StopContinueSignals.InterceptedScope(_fixture, hold ?? (_ => { }), interceptors);
        using var claim = RunGenerationFence.Claim(runId, generation);

        await scope.Resolve<ISupervisorTurnService>().RunTurnAsync(runId, teamId, "sup", Goal, conversationId: null, GoalConfig(capped), CancellationToken.None);
    });

    /// <summary>The turn's read of its own wave — the step after its stand-down check and before its wave's transaction.</summary>
    private static bool IsTurnWaveRead(string commandText) =>
        commandText.StartsWith("SELECT", StringComparison.Ordinal) && commandText.Contains("FROM workflow_run_wait") && commandText.Contains("'AgentRun'") && commandText.Contains("iteration_key LIKE");

    /// <summary>Stop the run in a transaction of the caller's scope, so the teardown waits in that scope's post-commit actions.</summary>
    private static async Task<CancelRunOutcome?> StopHoldingTeardownAsync(ILifetimeScope scope, Guid runId, Guid teamId)
    {
        await using var transaction = await scope.Resolve<CodeSpaceDbContext>().Database.BeginTransactionAsync();

        var outcome = await scope.Resolve<IWorkflowService>().CancelRunAsync(runId, teamId, CancellationToken.None);
        await transaction.CommitAsync();

        return outcome;
    }

    private async Task<Guid> SeedSupervisorRunAsync(Guid teamId, Guid userId, bool capped)
    {
        Guid workflowId;
        using (var scope = _fixture.BeginScopeAs(userId, teamId, Roles.Admin))
            workflowId = await scope.Resolve<IMediator>().Send(new CreateWorkflowCommand
            {
                Name = "sup-stop-continue-" + Guid.NewGuid().ToString("N")[..6],
                Description = null,
                Definition = SupervisorDefinition(capped),
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

    /// <summary>Drive the executor's terminal sequence (MarkRunning → Complete → Notify) without the sandboxed CLI, as <c>SupervisorSpawnFlowTests</c> does.</summary>
    private async Task SimulateAgentCompletionAsync(Guid agentRunId)
    {
        using var scope = _fixture.BeginScope();
        var runs = scope.Resolve<IAgentRunService>();

        await runs.MarkRunningAsync(agentRunId, CancellationToken.None);
        await runs.CompleteAsync(agentRunId, new AgentRunResult { Status = AgentRunStatus.Succeeded, ExitReason = "completed", Summary = "done" }, CancellationToken.None);
        await scope.Resolve<IAgentRunCompletionNotifier>().NotifyCompletedAsync(agentRunId, CancellationToken.None);
    }

    /// <summary>An unanswered, human-required question the agent raised mid-run — the row a stopped agent's cancel expires.</summary>
    private async Task<Guid> SeedOpenAgentDecisionAsync(Guid teamId, Guid agentRunId)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        var ledgerId = Guid.NewGuid();

        db.ToolCallLedger.Add(new ToolCallLedger
        {
            Id = ledgerId, TeamId = teamId, AgentRunId = agentRunId, ToolKind = DecisionToolKinds.DecisionRequest,
            IdempotencyKey = $"decision.request:{ledgerId:N}", InputHash = new string('0', 64),
            Status = ToolCallLedgerStatus.AwaitingApproval, ApprovalDeadlineAt = DateTimeOffset.UtcNow.AddHours(1),
            CreatedBy = SystemUsers.SeederId, LastModifiedBy = SystemUsers.SeederId,
        });

        await db.SaveChangesAsync();
        return ledgerId;
    }

    private async Task<ToolCallLedgerStatus> DecisionStatusAsync(Guid ledgerId)
    {
        using var scope = _fixture.BeginScope();
        return await scope.Resolve<CodeSpaceDbContext>().ToolCallLedger.AsNoTracking().Where(l => l.Id == ledgerId).Select(l => l.Status).SingleAsync();
    }

    private async Task<int> GenerationAsync(Guid runId)
    {
        using var scope = _fixture.BeginScope();
        return await scope.Resolve<CodeSpaceDbContext>().WorkflowRun.AsNoTracking().Where(r => r.Id == runId).Select(r => r.Generation).SingleAsync();
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

    /// <summary>The statuses of <paramref name="agentRunIds"/>, in the order given.</summary>
    private async Task<IReadOnlyList<AgentRunStatus>> AgentStatusesAsync(IReadOnlyList<Guid> agentRunIds)
    {
        using var scope = _fixture.BeginScope();
        var byId = await scope.Resolve<CodeSpaceDbContext>().AgentRun.AsNoTracking().Where(a => agentRunIds.Contains(a.Id)).ToDictionaryAsync(a => a.Id, a => a.Status);

        return agentRunIds.Where(byId.ContainsKey).Select(id => byId[id]).ToList();
    }

    private async Task<IReadOnlyList<string>> ReservationKeysAsync(Guid runId)
    {
        using var scope = _fixture.BeginScope();
        return await scope.Resolve<CodeSpaceDbContext>().BudgetReservation.AsNoTracking().Where(r => r.WorkflowRunId == runId).Select(r => r.ScopeKey).ToListAsync();
    }

    private async Task<int> NodeFailuresAsync(Guid runId, string nodeId)
    {
        using var scope = _fixture.BeginScope();
        return await scope.Resolve<CodeSpaceDbContext>().WorkflowRunRecord.AsNoTracking().CountAsync(r => r.RunId == runId && r.NodeId == nodeId && (r.RecordType == WorkflowRunRecordTypes.NodeFailed || r.RecordType == WorkflowRunRecordTypes.AttemptFailed));
    }

    private async Task<IReadOnlyList<SupervisorDecisionRecord>> DecisionsAsync(Guid runId, Guid teamId)
    {
        using var scope = _fixture.BeginScope();
        return await scope.Resolve<CodeSpaceDbContext>().SupervisorDecisionRecord.AsNoTracking().Where(d => d.SupervisorRunId == runId && d.TeamId == teamId).OrderBy(d => d.Sequence).ToListAsync();
    }

    private async Task<IReadOnlyList<string>> DecisionKindsAsync(Guid runId, Guid teamId) => (await DecisionsAsync(runId, teamId)).Select(d => d.DecisionKind).ToList();

    private async Task<IReadOnlyList<SupervisorDecisionRecord>> SpawnDecisionsAsync(Guid runId, Guid teamId) => (await DecisionsAsync(runId, teamId)).Where(d => d.DecisionKind == SupervisorDecisionKinds.Spawn).ToList();

    private async Task<SupervisorDecisionRecord> SpawnDecisionAsync(Guid runId, Guid teamId) => (await SpawnDecisionsAsync(runId, teamId)).ShouldHaveSingleItem("the run has one spawn decision");

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

    /// <summary>The iteration keys of a two-unit wave at <paramref name="turn"/> — also each slot's budget reservation scope.</summary>
    private static string[] WaveKeys(int turn) => new[] { $"sup#turn{turn}#0", $"sup#turn{turn}#1" };

    private static string SupervisorConfig(bool capped) => capped ? $$"""{"goal":"{{Goal}}","maxCostUsd":{{CapUsd}}}""" : $$"""{"goal":"{{Goal}}"}""";

    /// <summary>The goal config the supervisor node reads off its own config, for a turn driven through the turn service directly.</summary>
    private static SupervisorGoalConfig? GoalConfig(bool capped) => AgentSupervisorNode.ReadGoalConfig(JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(SupervisorConfig(capped))!);

    // manual → sup (agent.supervisor) → terminal. Shadow completion: the simulated agents mint no delivery evidence, and
    // completion arbitration is not this suite's subject (mirrors SupervisorSpawnFlowTests).
    private static WorkflowDefinition SupervisorDefinition(bool capped) => new()
    {
        SchemaVersion = 1,
        CompletionMode = WorkflowDefinition.CompletionModeShadow,
        Nodes = new List<NodeDefinition>
        {
            new() { Id = "start", TypeKey = "trigger.manual", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
            new() { Id = "sup", TypeKey = "agent.supervisor", Config = WorkflowsTestSeed.Json(SupervisorConfig(capped)), Inputs = WorkflowsTestSeed.EmptyJson() },
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
