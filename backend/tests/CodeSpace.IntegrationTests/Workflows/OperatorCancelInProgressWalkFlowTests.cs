using System.Data.Common;
using Autofac;
using CodeSpace.Core.Middlewares.Transactional;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Workflows;
using CodeSpace.Core.Services.Workflows.Engine;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.IntegrationTests.Infrastructure.Jobs;
using CodeSpace.IntegrationTests.Workflows.Infrastructure;
using CodeSpace.Messages.Commands.Workflows;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Dtos.Workflows;
using CodeSpace.Messages.Enums;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Shouldly;

namespace CodeSpace.IntegrationTests.Workflows;

/// <summary>
/// The durability audit's P3 fix: cancelling an ACTIVELY-RUNNING in-process engine walk is now cooperative. The
/// engine job is enqueued with <c>CancellationToken.None</c>, so before this an operator cancel flipped the row
/// Cancelled but the walk kept firing every remaining side-effecting node. Now the engine threads a per-run
/// <see cref="IRunCancellationRegistry"/> token through the walk and re-reads the run status at each wave
/// boundary, so a cancel mid-walk stops the remaining nodes from running.
///
/// <para>Integration tier (real Postgres + real engine): a blocking gate node parks the walk mid-flight so the
/// test can issue a REAL <c>WorkflowService.CancelRunAsync</c> while a wave is in progress, then assert the
/// downstream node never started and the run lands Cancelled. No model is exercised — deterministic.</para>
///
/// <para>The same gate pins the run-generation fence behind Continue: a walk a Continue overtook — on another host, or
/// with the stop's teardown still in flight — stops at its next wave check, and none of its run writes (a cancel, a
/// failure, a parent wake) lands on the revived run.</para>
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public class OperatorCancelInProgressWalkFlowTests
{
    private readonly PostgresFixture _fixture;

    public OperatorCancelInProgressWalkFlowTests(PostgresFixture fixture) { _fixture = fixture; }

    [Fact]
    public async Task Cancelling_an_in_progress_walk_stops_the_remaining_nodes()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var gateKey = Guid.NewGuid().ToString("N");
        var gate = CancelGateNode.Arm(gateKey);

        var workflowId = await CreateWorkflowAsync(teamId, userId, GatedChainDefinition(gateKey));
        var runId = await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, teamId);

        // Start the walk on a background task; it parks inside the gate node mid-wave (the run is now Running).
        var walk = Task.Run(async () =>
        {
            using var scope = _fixture.BeginScope();
            await scope.Resolve<IWorkflowEngine>().ExecuteRunAsync(runId, CancellationToken.None);
        });

        await gate.Started.Task.WaitAsync(TimeSpan.FromSeconds(30));

        // Operator cancel WHILE the walk is mid-flight, then let the gate finish.
        CancelRunOutcome? outcome;
        using (var scope = _fixture.BeginScope())
            outcome = await scope.Resolve<IWorkflowService>().CancelRunAsync(runId, teamId, CancellationToken.None);

        gate.Release.TrySetResult();

        try { await walk.WaitAsync(TimeSpan.FromSeconds(30)); }
        catch (OperationCanceledException) { /* the engine re-throws the cancel after handling it — expected */ }

        outcome.ShouldNotBeNull();
        outcome!.Cancelled.ShouldBeTrue();

        using var verify = _fixture.BeginScope();
        var db = verify.Resolve<CodeSpaceDbContext>();

        (await db.WorkflowRun.AsNoTracking().SingleAsync(r => r.Id == runId)).Status
            .ShouldBe(WorkflowRunStatus.Cancelled);

        (await NodeStartedCountAsync(db, runId, "gate")).ShouldBe(1, "the gate node was mid-flight when we cancelled");
        (await NodeStartedCountAsync(db, runId, "after")).ShouldBe(0, "cooperative cancel stopped the walk — the downstream node never fired");
    }

    [Fact]
    public async Task Cancel_trips_the_in_process_token_even_when_the_node_never_unblocks()
    {
        // Isolates the SAME-HOST registry-token path from the wave-boundary status re-read: the gate is NEVER
        // released, so the ONLY thing that can stop the walk is the cooperative token tripping the gate's
        // await (CancelGateNode awaits Release with the cancellationToken). If registry.Cancel were a no-op the
        // walk would hang here forever — the bounded WaitAsync proves the token actually reaches the running node.
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var gateKey = Guid.NewGuid().ToString("N");
        var gate = CancelGateNode.Arm(gateKey);

        var workflowId = await CreateWorkflowAsync(teamId, userId, GatedChainDefinition(gateKey));
        var runId = await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, teamId);

        var walk = Task.Run(async () =>
        {
            using var scope = _fixture.BeginScope();
            await scope.Resolve<IWorkflowEngine>().ExecuteRunAsync(runId, CancellationToken.None);
        });

        await gate.Started.Task.WaitAsync(TimeSpan.FromSeconds(30));

        using (var scope = _fixture.BeginScope())
            (await scope.Resolve<IWorkflowService>().CancelRunAsync(runId, teamId, CancellationToken.None))!.Cancelled.ShouldBeTrue();

        // NO gate.Release — only the token can unblock the gate. A timeout here = the token did NOT reach the node.
        try { await walk.WaitAsync(TimeSpan.FromSeconds(30)); }
        catch (OperationCanceledException) { /* engine re-throws after handling — expected */ }

        using var verify = _fixture.BeginScope();
        (await verify.Resolve<CodeSpaceDbContext>().WorkflowRun.AsNoTracking().SingleAsync(r => r.Id == runId)).Status
            .ShouldBe(WorkflowRunStatus.Cancelled, "the in-process token tripped the running node's await — no wave-boundary backstop was reachable");
    }

    [Fact]
    public async Task Cancelling_a_subworkflow_child_mid_walk_resumes_the_parent_instead_of_stranding_it()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var gateKey = Guid.NewGuid().ToString("N");
        var gate = CancelGateNode.Arm(gateKey);

        var childId = await CreateWorkflowAsync(teamId, userId, GatedChainDefinition(gateKey));
        var parentId = await CreateWorkflowAsync(teamId, userId, SubworkflowParentDefinition(childId));
        var parentRunId = await WorkflowsTestSeed.SeedManualRunAsync(_fixture, parentId, teamId);

        // Parent runs → suspends on the flow.subworkflow node, staging + dispatching the child.
        using (var scope = _fixture.BeginScope())
            await scope.Resolve<IWorkflowEngine>().ExecuteRunAsync(parentRunId, CancellationToken.None);

        Guid childRunId;
        using (var scope = _fixture.BeginScope())
        {
            var db = scope.Resolve<CodeSpaceDbContext>();
            (await db.WorkflowRun.AsNoTracking().SingleAsync(r => r.Id == parentRunId)).Status.ShouldBe(WorkflowRunStatus.Suspended);
            childRunId = (await db.WorkflowRun.AsNoTracking().SingleAsync(r => r.ParentRunId == parentRunId)).Id;
        }

        // Child runs on a background task; it parks inside the gate (the child is now Running mid-walk).
        var childWalk = Task.Run(async () =>
        {
            using var scope = _fixture.BeginScope();
            await scope.Resolve<IWorkflowEngine>().ExecuteRunAsync(childRunId, CancellationToken.None);
        });

        await gate.Started.Task.WaitAsync(TimeSpan.FromSeconds(30));

        // Cancel the CHILD while it is mid-walk.
        using (var scope = _fixture.BeginScope())
            (await scope.Resolve<IWorkflowService>().CancelRunAsync(childRunId, teamId, CancellationToken.None))!.Cancelled.ShouldBeTrue();

        gate.Release.TrySetResult();
        try { await childWalk.WaitAsync(TimeSpan.FromSeconds(30)); }
        catch (OperationCanceledException) { }

        // The fix: the cancelled child resumed its parent. Its subworkflow wait is Resolved and the parent was
        // re-dispatched; run it and assert it LEFT Suspended (without the fix it would stay Suspended forever).
        using (var scope = _fixture.BeginScope())
            await scope.Resolve<IWorkflowEngine>().ExecuteRunAsync(parentRunId, CancellationToken.None);

        using var verify = _fixture.BeginScope();
        var verifyDb = verify.Resolve<CodeSpaceDbContext>();

        (await verifyDb.WorkflowRun.AsNoTracking().SingleAsync(r => r.Id == childRunId)).Status.ShouldBe(WorkflowRunStatus.Cancelled);

        (await verifyDb.WorkflowRunWait.AsNoTracking()
            .Where(w => w.RunId == parentRunId && w.WaitKind == WorkflowWaitKinds.Subworkflow)
            .Select(w => w.Status).FirstAsync())
            .ShouldBe(WorkflowWaitStatuses.Resolved, "the cancelled child resolved its parent's subworkflow wait");

        (await verifyDb.WorkflowRun.AsNoTracking().SingleAsync(r => r.Id == parentRunId)).Status
            .ShouldNotBe(WorkflowRunStatus.Suspended, "the parent is not stranded Suspended forever — the cancelled child woke it");
    }

    [Fact]
    public async Task Continuing_a_stopped_run_resumes_the_interrupted_frontier_to_completion()
    {
        // The counterpart to the cancel tests above: after an operator STOP lands the run Cancelled with the gate node
        // left mid-flight (Running), ContinueRunAsync re-runs that interrupted frontier IN PLACE (same run id) and drives
        // it to Success — reusing the succeeded upstream, never re-running it. Proves ContinueCancelledRunAsync.
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var gateKey = Guid.NewGuid().ToString("N");
        var gate = CancelGateNode.Arm(gateKey);

        var workflowId = await CreateWorkflowAsync(teamId, userId, GatedChainDefinition(gateKey));
        var runId = await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, teamId);

        // Start the walk; it parks inside the gate mid-wave (the run is Running, the gate node Running).
        var walk = Task.Run(async () =>
        {
            using var scope = _fixture.BeginScope();
            await scope.Resolve<IWorkflowEngine>().ExecuteRunAsync(runId, CancellationToken.None);
        });
        await gate.Started.Task.WaitAsync(TimeSpan.FromSeconds(30));

        // Operator STOP while mid-flight → Cancelled, the gate node left Running (interrupted — its work never finished).
        using (var scope = _fixture.BeginScope())
            (await scope.Resolve<IWorkflowService>().CancelRunAsync(runId, teamId, CancellationToken.None))!.Cancelled.ShouldBeTrue();

        gate.Release.TrySetResult();   // unblock the first walk's gate await so it unwinds through the cancel
        try { await walk.WaitAsync(TimeSpan.FromSeconds(30)); }
        catch (OperationCanceledException) { /* the engine re-throws the cancel after handling — expected */ }

        using (var verify = _fixture.BeginScope())
        {
            var db = verify.Resolve<CodeSpaceDbContext>();
            (await db.WorkflowRun.AsNoTracking().SingleAsync(r => r.Id == runId)).Status.ShouldBe(WorkflowRunStatus.Cancelled);
            (await NodeStartedCountAsync(db, runId, "after")).ShouldBe(0, "the stop halted the walk before the downstream node ever ran");
        }

        // CONTINUE the stopped run — flips Cancelled → Pending and resets the interrupted gate node so the re-walk
        // re-runs it (the gate's Release is already set, so it no longer parks) and drives on to `after` → `end`.
        bool continued;
        using (var scope = _fixture.BeginScope())
            continued = await scope.Resolve<IWorkflowService>().ContinueRunAsync(runId, teamId, CancellationToken.None);

        continued.ShouldBeTrue("a stopped run with an interrupted frontier continues in place (same run id, never a fork)");

        // Dispatch fired inline post-commit (leaving the run Enqueued); drive the re-walk exactly as the rerun tests do.
        using (var scope = _fixture.BeginScope())
            await scope.Resolve<IWorkflowEngine>().ExecuteRunAsync(runId, CancellationToken.None);

        using var final = _fixture.BeginScope();
        var fdb = final.Resolve<CodeSpaceDbContext>();

        (await fdb.WorkflowRun.AsNoTracking().SingleAsync(r => r.Id == runId)).Status
            .ShouldBe(WorkflowRunStatus.Success, "the continued run drove the interrupted frontier + downstream to completion — the reset frontier re-ran and finished");
        (await NodeStartedCountAsync(fdb, runId, "start")).ShouldBe(1, "the succeeded trigger upstream was REUSED, never re-run (no duplicate of completed work)");
        (await NodeStartedCountAsync(fdb, runId, "after")).ShouldBe(1, "the downstream node ran exactly once after the frontier resumed (it never ran before the stop)");
        (await NodeStartedCountAsync(fdb, runId, "gate")).ShouldBeGreaterThanOrEqualTo(2, "the interrupted gate node was reset and re-ran on continue (it was Running when stopped)");
    }

    [Fact]
    public async Task A_walk_a_continue_overtook_stops_at_its_next_check_and_no_step_runs_twice()
    {
        // The stop's teardown is held back, the way a slow kill-wave holds it, so nothing trips the first walk's token:
        // only its wave-boundary check can notice the stop. Continue revives the run and a second walk claims it before
        // the first walk's step returns. The run is Running again when that check runs — the second walk's Running.
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var gateKey = Guid.NewGuid().ToString("N");
        var firstGate = CancelGateNode.Arm(gateKey);

        var workflowId = await CreateWorkflowAsync(teamId, userId, GatedChainDefinition(gateKey));
        var runId = await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, teamId);

        var firstWalk = WalkInBackground(runId, CancellationToken.None);
        await firstGate.Started.Task.WaitAsync(TimeSpan.FromSeconds(30));

        using var stop = await WorkflowsTestSeed.StopWithTeardownHeldAsync(_fixture, runId, teamId);
        await ContinueAsync(runId, teamId);

        var secondGate = CancelGateNode.Arm(gateKey);
        var secondWalk = WalkInBackground(runId, CancellationToken.None);
        await secondGate.Started.Task.WaitAsync(TimeSpan.FromSeconds(30));

        // The held teardown lands while the revived walk is mid-step on this same host: it must not trip that walk.
        await stop.Resolve<IPostCommitActions>().RunAllAsync(CancellationToken.None);

        firstGate.Release.TrySetResult();
        var overtaken = await Record.ExceptionAsync(() => firstWalk.WaitAsync(TimeSpan.FromSeconds(30)));

        secondGate.Release.TrySetResult();
        var revived = await Record.ExceptionAsync(() => secondWalk.WaitAsync(TimeSpan.FromSeconds(30)));

        overtaken.ShouldBeNull("the overtaken walk stands down quietly — it neither cancels nor fails a run that is no longer its own");
        revived.ShouldBeNull("the revived walk ran to the end — the late teardown never tripped it");

        using var verify = _fixture.BeginScope();
        var db = verify.Resolve<CodeSpaceDbContext>();

        (await db.WorkflowRun.AsNoTracking().SingleAsync(r => r.Id == runId)).Status.ShouldBe(WorkflowRunStatus.Success, "the revived walk finished the run — neither the late teardown nor the overtaken walk ended it");
        (await NodeStartedCountAsync(db, runId, "after")).ShouldBe(1, "the overtaken walk stopped at its next check, so the step after the gate ran once — in the revived walk");
    }

    [Fact]
    public async Task A_walk_a_continue_overtook_never_cancels_the_revived_run_as_it_unwinds()
    {
        // The overtaken walk runs on another host (its own cancellation registry, so the stop never reaches it) and only
        // unwinds through the cancel path — its host shutting down — after a second walk claimed the revived run. Its
        // "land the run Cancelled" write must lose to the newer generation.
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var gateKey = Guid.NewGuid().ToString("N");
        var firstGate = CancelGateNode.Arm(gateKey);

        var workflowId = await CreateWorkflowAsync(teamId, userId, GatedChainDefinition(gateKey));
        var runId = await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, teamId);

        using var otherHostShutdown = new CancellationTokenSource();
        var firstWalk = WalkOnAnotherHostInBackground(runId, otherHostShutdown.Token);
        await firstGate.Started.Task.WaitAsync(TimeSpan.FromSeconds(30));

        using (var scope = _fixture.BeginScope())
            (await scope.Resolve<IWorkflowService>().CancelRunAsync(runId, teamId, CancellationToken.None))!.Cancelled.ShouldBeTrue();

        await ContinueAsync(runId, teamId);

        var secondGate = CancelGateNode.Arm(gateKey);
        var secondWalk = WalkInBackground(runId, CancellationToken.None);
        await secondGate.Started.Task.WaitAsync(TimeSpan.FromSeconds(30));

        otherHostShutdown.Cancel();
        var overtaken = await Record.ExceptionAsync(() => firstWalk.WaitAsync(TimeSpan.FromSeconds(30)));

        secondGate.Release.TrySetResult();
        var revived = await Record.ExceptionAsync(() => secondWalk.WaitAsync(TimeSpan.FromSeconds(30)));

        overtaken.ShouldBeAssignableTo<OperationCanceledException>("the overtaken walk unwound through the cancel path");
        revived.ShouldBeNull("the revived walk ran to the end — the overtaken walk's unwind never stopped it");

        using var verify = _fixture.BeginScope();
        var db = verify.Resolve<CodeSpaceDbContext>();

        (await db.WorkflowRun.AsNoTracking().SingleAsync(r => r.Id == runId)).Status.ShouldBe(WorkflowRunStatus.Success, "the revived run finished — the overtaken walk's unwind did not cancel it");
        (await db.WorkflowRunRecord.AsNoTracking().CountAsync(r => r.RunId == runId && r.RecordType == WorkflowRunRecordTypes.RunCancelled)).ShouldBe(1, "only the operator's stop is on the tape — the overtaken walk landed no cancel of its own");
    }

    [Fact]
    public async Task A_walk_a_continue_overtook_in_its_last_wave_never_fails_the_revived_run()
    {
        // The gate shares its wave with the terminal, so when the overtaken walk's gate returns there is no next wave to
        // check at: it goes straight to its terminal write. That tracked save loses to the revived run's newer row, and
        // the failure it falls into must not land on the revived run either.
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var gateKey = Guid.NewGuid().ToString("N");
        var firstGate = CancelGateNode.Arm(gateKey);

        var workflowId = await CreateWorkflowAsync(teamId, userId, GateBesideTerminalDefinition(gateKey));
        var runId = await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, teamId);

        var firstWalk = WalkOnAnotherHostInBackground(runId, CancellationToken.None);
        await firstGate.Started.Task.WaitAsync(TimeSpan.FromSeconds(30));

        using (var scope = _fixture.BeginScope())
            (await scope.Resolve<IWorkflowService>().CancelRunAsync(runId, teamId, CancellationToken.None))!.Cancelled.ShouldBeTrue();

        await ContinueAsync(runId, teamId);

        var secondGate = CancelGateNode.Arm(gateKey);
        var secondWalk = WalkInBackground(runId, CancellationToken.None);
        await secondGate.Started.Task.WaitAsync(TimeSpan.FromSeconds(30));

        firstGate.Release.TrySetResult();
        await Record.ExceptionAsync(() => firstWalk.WaitAsync(TimeSpan.FromSeconds(30)));

        secondGate.Release.TrySetResult();
        var revived = await Record.ExceptionAsync(() => secondWalk.WaitAsync(TimeSpan.FromSeconds(30)));

        revived.ShouldBeNull("the revived walk ran to the end");

        using var verify = _fixture.BeginScope();
        var db = verify.Resolve<CodeSpaceDbContext>();

        (await db.WorkflowRun.AsNoTracking().SingleAsync(r => r.Id == runId)).Status.ShouldBe(WorkflowRunStatus.Success, "the revived walk finished the run — the overtaken walk's lost terminal write did not fail it");
        (await db.WorkflowRunRecord.AsNoTracking().CountAsync(r => r.RunId == runId && r.RecordType == WorkflowRunRecordTypes.RunFailed)).ShouldBe(0, "nothing on the tape claims the run failed");
    }

    [Fact]
    public async Task A_walk_a_continue_overtook_during_its_bootstrap_never_fails_the_revived_run()
    {
        // A continued run's walk claims it and is still bootstrapping when the operator stops and continues it again. The
        // newer walk claims the run and parks mid-step; only then does the overtaken walk's bootstrap fail. Its "land the
        // run Failure" write — reached with nothing dirty that could roll it back — must lose to the newer generation.
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var gateKey = Guid.NewGuid().ToString("N");

        var workflowId = await CreateWorkflowAsync(teamId, userId, FlakyThenGatedChainDefinition(Guid.NewGuid().ToString("N"), gateKey));
        var runId = await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, teamId);

        await WalkInBackground(runId, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(30));   // the flaky step fails once → Failure
        await ContinueAsync(runId, teamId);

        var heldBootstrap = new HeldDefinitionReadFault();
        var overtakenWalk = WalkWithFaultInBackground(runId, heldBootstrap);
        await heldBootstrap.Reached.Task.WaitAsync(TimeSpan.FromSeconds(30));

        using (var scope = _fixture.BeginScope())
            (await scope.Resolve<IWorkflowService>().CancelRunAsync(runId, teamId, CancellationToken.None))!.Cancelled.ShouldBeTrue();

        await ContinueAsync(runId, teamId);

        var gate = CancelGateNode.Arm(gateKey);
        var revivedWalk = WalkInBackground(runId, CancellationToken.None);
        await gate.Started.Task.WaitAsync(TimeSpan.FromSeconds(30));

        heldBootstrap.Release.TrySetResult();
        await Record.ExceptionAsync(() => overtakenWalk.WaitAsync(TimeSpan.FromSeconds(30)));

        gate.Release.TrySetResult();
        var revived = await Record.ExceptionAsync(() => revivedWalk.WaitAsync(TimeSpan.FromSeconds(30)));

        revived.ShouldBeNull("the revived walk ran to the end");

        using var verify = _fixture.BeginScope();
        var db = verify.Resolve<CodeSpaceDbContext>();

        (await db.WorkflowRun.AsNoTracking().SingleAsync(r => r.Id == runId)).Status.ShouldBe(WorkflowRunStatus.Success, "the revived walk finished the run — the overtaken walk's bootstrap failure did not fail it");
        (await db.WorkflowRunRecord.AsNoTracking().CountAsync(r => r.RunId == runId && r.RecordType == WorkflowRunRecordTypes.RunFailed)).ShouldBe(1, "only the first attempt's real failure is on the tape");
    }

    [Fact]
    public async Task A_walk_a_continue_overtook_inside_a_loop_body_stands_down_at_its_next_body_wave()
    {
        // The stop lands on another host, so only the database can tell the old walk it was overtaken. Inside a loop
        // body nothing asked it: the walk kept going under the revived run while the revived walk re-ran the same loop,
        // so every body step after the one in flight ran twice.
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var gateKey = Guid.NewGuid().ToString("N");
        var firstGate = CancelGateNode.Arm(gateKey);

        var workflowId = await CreateWorkflowAsync(teamId, userId, LoopOverGateDefinition(gateKey));
        var runId = await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, teamId);

        await AssertOvertakenBodyWalkStandsDownAsync(runId, teamId, firstGate, gateKey, "the body step after the gate ran once — the overtaken walk stood down at its next body wave");
    }

    [Fact]
    public async Task A_walk_a_continue_overtook_inside_a_map_branch_stands_down_at_its_next_branch_wave()
    {
        // The same inside a map branch, which runs on its own child-scope engine: the branch has to know the generation
        // its walk claimed to notice it has moved.
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var gateKey = Guid.NewGuid().ToString("N");
        var firstGate = CancelGateNode.Arm(gateKey);

        var workflowId = await CreateWorkflowAsync(teamId, userId, MapOverGateDefinition(gateKey));
        var runId = await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, teamId, payloadJson: """{ "things": ["a"] }""");

        await AssertOvertakenBodyWalkStandsDownAsync(runId, teamId, firstGate, gateKey, "the branch step after the gate ran once — the overtaken walk stood down at its next branch wave");
    }

    [Fact]
    public async Task A_step_a_continue_overtook_that_parks_afterwards_stages_nothing_and_leaves_the_revived_wait()
    {
        // The overtaken walk's step was in flight when the run was stopped and continued; the revived walk re-ran it and
        // parked the cell first. When the old step finally parks it must stage no agent and leave the revived wait alone.
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var gateKey = Guid.NewGuid().ToString("N");
        var firstGate = GatedAgentParkNode.Arm(gateKey);

        var workflowId = await CreateWorkflowAsync(teamId, userId, GatedAgentParkDefinition(gateKey));
        var runId = await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, teamId);

        using var manual = ResolveJobClient().ManualExecution();   // the executor dispatch is recorded, never run

        var firstWalk = WalkOnAnotherHostInBackground(runId, CancellationToken.None);
        await firstGate.Started.Task.WaitAsync(TimeSpan.FromSeconds(30));

        await StopAsync(runId, teamId);
        await ContinueAsync(runId, teamId);

        GatedAgentParkNode.Arm(gateKey).Release.TrySetResult();   // the revived walk's step passes straight through
        await WalkInBackground(runId, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(30));
        var revivedWait = await AgentWaitAsync(runId);

        firstGate.Release.TrySetResult();
        var overtaken = await Record.ExceptionAsync(() => firstWalk.WaitAsync(TimeSpan.FromSeconds(30)));

        overtaken.ShouldBeNull("the overtaken step stood down at its park, writing nothing");

        using var verify = _fixture.BeginScope();
        var db = verify.Resolve<CodeSpaceDbContext>();

        (await AgentWaitAsync(runId)).Id.ShouldBe(revivedWait.Id, "the revived walk's wait for the cell survived the overtaken step's park");
        (await db.AgentRun.AsNoTracking().Where(a => a.WorkflowRunId == runId).Select(a => a.Id).ToListAsync())
            .ShouldHaveSingleItem("the overtaken step staged no agent").ShouldBe(Guid.Parse(revivedWait.Token), "the one agent is the revived walk's");
    }

    [Fact]
    public async Task A_step_overtaken_mid_park_undoes_what_it_staged_and_leaves_the_revived_wait()
    {
        // Narrower: the overtaken step passed its park's generation check BEFORE the stop and was overtaken while it
        // staged. Its wait write must be refused — the revived walk already parked the cell — and the agent it had just
        // staged cancelled, not left Queued against the admission cap under the revived run.
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var gateKey = Guid.NewGuid().ToString("N");
        var firstGate = GatedAgentParkNode.Arm(gateKey);

        var workflowId = await CreateWorkflowAsync(teamId, userId, GatedAgentParkDefinition(gateKey));
        var runId = await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, teamId);

        using var manual = ResolveJobClient().ManualExecution();

        var heldCheck = new HeldGenerationCheckFault();
        var firstWalk = WalkWithFaultInBackground(runId, heldCheck);
        await firstGate.Started.Task.WaitAsync(TimeSpan.FromSeconds(30));

        heldCheck.Arm();
        firstGate.Release.TrySetResult();
        await heldCheck.Reached.Task.WaitAsync(TimeSpan.FromSeconds(30));   // passed its park's check (nothing stopped yet), held right after it

        await StopAsync(runId, teamId);
        await ContinueAsync(runId, teamId);

        GatedAgentParkNode.Arm(gateKey).Release.TrySetResult();
        await WalkInBackground(runId, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(30));
        var revivedWait = await AgentWaitAsync(runId);

        heldCheck.Release.TrySetResult();
        await Record.ExceptionAsync(() => firstWalk.WaitAsync(TimeSpan.FromSeconds(30)));

        using var verify = _fixture.BeginScope();
        var db = verify.Resolve<CodeSpaceDbContext>();

        (await AgentWaitAsync(runId)).Id.ShouldBe(revivedWait.Id, "the overtaken step's wait write was refused — the revived wait for the cell survived");

        var agents = await db.AgentRun.AsNoTracking().Where(a => a.WorkflowRunId == runId).Select(a => new { a.Id, a.Status }).ToListAsync();
        agents.Single(a => a.Id == Guid.Parse(revivedWait.Token)).Status.ShouldBe(AgentRunStatus.Queued, "the revived walk's agent is untouched");
        agents.Where(a => a.Id != Guid.Parse(revivedWait.Token)).Select(a => a.Status)
            .ShouldBe(new[] { AgentRunStatus.Cancelled }, "the one agent the overtaken step staged was cancelled when its park was refused");
    }

    [Fact]
    public async Task A_child_walk_a_continue_overtook_does_not_wake_the_parent_with_a_cancel_as_it_unwinds()
    {
        // A cancelled child's walk wakes its parked parent with the cancel as it unwinds. When a Continue revived the
        // child first, that cancel is no longer true: the parent must keep waiting, and be woken by the child's real end.
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var gateKey = Guid.NewGuid().ToString("N");

        var childId = await CreateWorkflowAsync(teamId, userId, GatedChainDefinition(gateKey));
        var parentId = await CreateWorkflowAsync(teamId, userId, SubworkflowParentDefinition(childId));
        var parentRunId = await WorkflowsTestSeed.SeedManualRunAsync(_fixture, parentId, teamId);

        await WalkInBackground(parentRunId, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(30));   // parks on the child

        Guid childRunId;
        using (var scope = _fixture.BeginScope())
            childRunId = (await scope.Resolve<CodeSpaceDbContext>().WorkflowRun.AsNoTracking().SingleAsync(r => r.ParentRunId == parentRunId)).Id;

        var firstGate = CancelGateNode.Arm(gateKey);
        using var otherHostShutdown = new CancellationTokenSource();
        var firstWalk = WalkOnAnotherHostInBackground(childRunId, otherHostShutdown.Token);
        await firstGate.Started.Task.WaitAsync(TimeSpan.FromSeconds(30));

        using (var scope = _fixture.BeginScope())
            (await scope.Resolve<IWorkflowService>().CancelRunAsync(childRunId, teamId, CancellationToken.None))!.Cancelled.ShouldBeTrue();

        await ContinueAsync(childRunId, teamId);

        var secondGate = CancelGateNode.Arm(gateKey);
        var secondWalk = WalkInBackground(childRunId, CancellationToken.None);
        await secondGate.Started.Task.WaitAsync(TimeSpan.FromSeconds(30));

        otherHostShutdown.Cancel();
        await Record.ExceptionAsync(() => firstWalk.WaitAsync(TimeSpan.FromSeconds(30)));

        var afterUnwind = await ParentWaitAsync(parentRunId);

        secondGate.Release.TrySetResult();
        await secondWalk.WaitAsync(TimeSpan.FromSeconds(30));

        var afterChildEnded = await ParentWaitAsync(parentRunId);

        afterUnwind.Status.ShouldBe(WorkflowWaitStatuses.Pending, "the overtaken child walk's unwind did not wake the parent with a cancel that is no longer true");
        afterChildEnded.Status.ShouldBe(WorkflowWaitStatuses.Resolved, "the revived child's own end woke the parent");
        afterChildEnded.PayloadJson!.ShouldContain(nameof(WorkflowRunStatus.Success), customMessage: "with the child's real outcome");
    }

    [Fact]
    public async Task A_continued_run_shows_no_finish_time_or_error_until_it_finishes_again()
    {
        // Continue revives a finished run in place. While it runs again the previous attempt's finish time and error no
        // longer describe it, so the run detail must not keep showing them; the next finish writes its own.
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var gateKey = Guid.NewGuid().ToString("N");

        var workflowId = await CreateWorkflowAsync(teamId, userId, FlakyThenGatedChainDefinition(Guid.NewGuid().ToString("N"), gateKey));
        var runId = await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, teamId);

        await WalkInBackground(runId, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(30));   // the flaky step fails once → Failure

        var failed = await RunDetailAsync(runId, teamId);
        failed.Status.ShouldBe(WorkflowRunStatus.Failure, "precondition: the first attempt failed");
        failed.CompletedAt.ShouldNotBeNull();
        failed.Error.ShouldNotBeNull();

        var gate = CancelGateNode.Arm(gateKey);
        await ContinueAsync(runId, teamId);

        var walk = WalkInBackground(runId, CancellationToken.None);
        await gate.Started.Task.WaitAsync(TimeSpan.FromSeconds(30));

        var live = await RunDetailAsync(runId, teamId);

        gate.Release.TrySetResult();
        await walk.WaitAsync(TimeSpan.FromSeconds(30));

        live.Status.ShouldBe(WorkflowRunStatus.Running, "precondition: the continued run was mid-step");
        live.CompletedAt.ShouldBeNull("a run that is running again has not finished — the previous attempt's finish time no longer describes it");
        live.Error.ShouldBeNull("nor does the previous attempt's error");

        var finished = await RunDetailAsync(runId, teamId);
        finished.Status.ShouldBe(WorkflowRunStatus.Success);
        finished.CompletedAt.ShouldNotBeNull();
        finished.CompletedAt!.Value.ShouldBeGreaterThan(failed.CompletedAt!.Value, "the finish time shown is the second attempt's own");
        finished.Error.ShouldBeNull();
    }

    private Task WalkInBackground(Guid runId, CancellationToken hostToken) => Task.Run(async () =>
    {
        using var scope = _fixture.BeginScope();
        await scope.Resolve<IWorkflowEngine>().ExecuteRunAsync(runId, hostToken);
    });

    /// <summary>A walk on another replica: its own in-process cancellation registry, which a stop landing on this host never trips.</summary>
    private Task WalkOnAnotherHostInBackground(Guid runId, CancellationToken hostToken) => Task.Run(async () =>
    {
        using var scope = _fixture.BeginScope(b => b.RegisterType<RunCancellationRegistry>().As<IRunCancellationRegistry>().SingleInstance());
        await scope.Resolve<IWorkflowEngine>().ExecuteRunAsync(runId, hostToken);
    });

    /// <summary>A walk on another replica whose database commands pass through <paramref name="fault"/> — the seam that holds it at an exact step.</summary>
    private Task WalkWithFaultInBackground(Guid runId, IInterceptor fault)
    {
        DbContextOptions<CodeSpaceDbContext> production;
        using (var probe = _fixture.BeginScope())
            production = probe.Resolve<DbContextOptions<CodeSpaceDbContext>>();

        var options = new DbContextOptionsBuilder<CodeSpaceDbContext>(production).AddInterceptors(fault).Options;

        return Task.Run(async () =>
        {
            using var scope = _fixture.BeginScope(b =>
            {
                b.RegisterInstance(options).As<DbContextOptions<CodeSpaceDbContext>>().SingleInstance();
                b.RegisterType<RunCancellationRegistry>().As<IRunCancellationRegistry>().SingleInstance();
            });
            await scope.Resolve<IWorkflowEngine>().ExecuteRunAsync(runId, CancellationToken.None);
        });
    }

    /// <summary>Holds the scope's first read of its pinned definition — a step the engine takes after its claim, during bootstrap — until released, then fails it.</summary>
    private sealed class HeldDefinitionReadFault : DbCommandInterceptor
    {
        private int _held;

        public TaskCompletionSource Reached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (!command.CommandText.Contains("FROM workflow_version") || Interlocked.Exchange(ref _held, 1) == 1) return result;

            Reached.TrySetResult();
            await Release.Task.ConfigureAwait(false);

            throw new InvalidOperationException("The definition read failed after a Continue overtook this walk.");
        }
    }

    /// <summary>Once armed, holds the scope's next generation check — the read a step's park makes before it stages anything — right AFTER it returns, so the step carries on from what it read while the test stops and continues the run.</summary>
    private sealed class HeldGenerationCheckFault : DbCommandInterceptor
    {
        private int _armed;

        public TaskCompletionSource Reached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Arm() => Interlocked.Exchange(ref _armed, 1);

        public override async ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command, CommandExecutedEventData eventData, DbDataReader result, CancellationToken cancellationToken = default)
        {
            if (!IsGenerationCheck(command) || Interlocked.CompareExchange(ref _armed, 0, 1) != 1) return result;

            Reached.TrySetResult();
            await Release.Task.ConfigureAwait(false);

            return result;
        }

        /// <summary>The superseded check's own predicate — an entity read of the run selects the generation column too, but never compares it.</summary>
        private static bool IsGenerationCheck(DbCommand command) => command.CommandText.Contains("FROM workflow_run AS") && command.CommandText.Contains("generation <>");
    }

    /// <summary>
    /// The body-level twin of the top-level overtaken-walk test: the old walk (on another host) holds mid-body in the gate;
    /// the run is stopped and continued; the revived walk re-runs the container and holds in the same gate; the old walk's
    /// gate returns first. Only the revived walk may run the body step after the gate.
    /// </summary>
    private async Task AssertOvertakenBodyWalkStandsDownAsync(Guid runId, Guid teamId, CancelGateNode.Gate firstGate, string gateKey, string afterRanOnce)
    {
        var firstWalk = WalkOnAnotherHostInBackground(runId, CancellationToken.None);
        await firstGate.Started.Task.WaitAsync(TimeSpan.FromSeconds(30));

        await StopAsync(runId, teamId);
        await ContinueAsync(runId, teamId);

        var secondGate = CancelGateNode.Arm(gateKey);
        var secondWalk = WalkInBackground(runId, CancellationToken.None);
        await secondGate.Started.Task.WaitAsync(TimeSpan.FromSeconds(30));

        firstGate.Release.TrySetResult();
        var overtaken = await Record.ExceptionAsync(() => firstWalk.WaitAsync(TimeSpan.FromSeconds(30)));

        secondGate.Release.TrySetResult();
        var revived = await Record.ExceptionAsync(() => secondWalk.WaitAsync(TimeSpan.FromSeconds(30)));

        overtaken.ShouldBeNull("the overtaken walk stood down inside the container, writing nothing");
        revived.ShouldBeNull("the revived walk ran the container to the end");

        using var verify = _fixture.BeginScope();
        var db = verify.Resolve<CodeSpaceDbContext>();

        (await db.WorkflowRun.AsNoTracking().SingleAsync(r => r.Id == runId)).Status.ShouldBe(WorkflowRunStatus.Success, "the revived walk finished the run");
        (await NodeStartedCountAsync(db, runId, "after")).ShouldBe(1, afterRanOnce);
    }

    private async Task StopAsync(Guid runId, Guid teamId)
    {
        using var scope = _fixture.BeginScope();
        (await scope.Resolve<IWorkflowService>().CancelRunAsync(runId, teamId, CancellationToken.None))!.Cancelled.ShouldBeTrue("the run was stopped");
    }

    /// <summary>The run's one Pending AgentRun wait — the cell a parking step owns.</summary>
    private async Task<WorkflowRunWait> AgentWaitAsync(Guid runId)
    {
        using var scope = _fixture.BeginScope();
        return await scope.Resolve<CodeSpaceDbContext>().WorkflowRunWait.AsNoTracking().SingleAsync(w => w.RunId == runId && w.WaitKind == WorkflowWaitKinds.AgentRun && w.Status == WorkflowWaitStatuses.Pending);
    }

    private InMemoryBackgroundJobClient ResolveJobClient()
    {
        using var scope = _fixture.BeginScope();
        return scope.Resolve<InMemoryBackgroundJobClient>();
    }

    private async Task ContinueAsync(Guid runId, Guid teamId)
    {
        using var scope = _fixture.BeginScope();
        (await scope.Resolve<IWorkflowService>().ContinueRunAsync(runId, teamId, CancellationToken.None)).ShouldBeTrue("the run continues in place");
    }

    private async Task<WorkflowRunWait> ParentWaitAsync(Guid parentRunId)
    {
        using var scope = _fixture.BeginScope();
        return await scope.Resolve<CodeSpaceDbContext>().WorkflowRunWait.AsNoTracking().SingleAsync(w => w.RunId == parentRunId && w.WaitKind == WorkflowWaitKinds.Subworkflow);
    }

    private async Task<WorkflowRunDetail> RunDetailAsync(Guid runId, Guid teamId)
    {
        using var scope = _fixture.BeginScope();
        return (await scope.Resolve<IWorkflowService>().GetRunAsync(runId, teamId, CancellationToken.None)).ShouldNotBeNull();
    }

    // start → loop(maxIterations 1; body: ls → gate → after) → end. The walk holds in the gate mid-body; `after` is the
    // body step that must run exactly once.
    private static WorkflowDefinition LoopOverGateDefinition(string gateKey) => new()
    {
        SchemaVersion = 1,
        Nodes = new List<NodeDefinition>
        {
            new() { Id = "start", TypeKey = "trigger.manual", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
            new() { Id = "loop", TypeKey = "flow.loop", Config = WorkflowsTestSeed.Json("""{ "maxIterations": 1 }"""), Inputs = WorkflowsTestSeed.EmptyJson() },
            new() { Id = "ls", TypeKey = "flow.loop_start", ParentId = "loop", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
            new() { Id = "gate", TypeKey = CancelGateNode.Key, ParentId = "loop", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.Json($$"""{ "gate": "{{gateKey}}" }""") },
            new() { Id = "after", TypeKey = JsonEmitNode.Key, ParentId = "loop", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.Json("""{ "ran": true }""") },
            new() { Id = "end", TypeKey = "builtin.terminal", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
        },
        Edges = new List<EdgeDefinition>
        {
            new() { From = "start", To = "loop" },
            new() { From = "loop", To = "end" },
            new() { From = "ls", To = "gate" },
            new() { From = "gate", To = "after" },
        },
    };

    // start → map(items = trigger.things; body: ms → gate → after) → end. `after` is the branch terminal — the element's result.
    private static WorkflowDefinition MapOverGateDefinition(string gateKey) => new()
    {
        SchemaVersion = 1,
        Nodes = new List<NodeDefinition>
        {
            new() { Id = "start", TypeKey = "trigger.manual", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
            new() { Id = "map", TypeKey = "flow.map", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.Json("""{ "items": "{{trigger.things}}" }""") },
            new() { Id = "ms", TypeKey = "flow.map_start", ParentId = "map", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
            new() { Id = "gate", TypeKey = CancelGateNode.Key, ParentId = "map", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.Json($$"""{ "gate": "{{gateKey}}" }""") },
            new() { Id = "after", TypeKey = JsonEmitNode.Key, ParentId = "map", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.Json("""{ "ran": true }""") },
            new() { Id = "end", TypeKey = "builtin.terminal", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
        },
        Edges = new List<EdgeDefinition>
        {
            new() { From = "start", To = "map" },
            new() { From = "map", To = "end" },
            new() { From = "ms", To = "gate" },
            new() { From = "gate", To = "after" },
        },
    };

    // start → park (holds on its gate, then parks on an AgentRun wait) → end.
    private static WorkflowDefinition GatedAgentParkDefinition(string gateKey) => new()
    {
        SchemaVersion = 1,
        Nodes = new List<NodeDefinition>
        {
            new() { Id = "start", TypeKey = "trigger.manual", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
            new() { Id = "park", TypeKey = GatedAgentParkNode.Key, Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.Json($$"""{ "gate": "{{gateKey}}" }""") },
            new() { Id = "end", TypeKey = "builtin.terminal", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
        },
        Edges = new List<EdgeDefinition>
        {
            new() { From = "start", To = "park" },
            new() { From = "park", To = "end" },
        },
    };

    // start → { gate, end }: the terminal runs in the gate's own wave, so the walk's LAST wave is the gate's — when the gate
    // returns the walk goes straight to its terminal write, with no wave boundary left to check at.
    private static WorkflowDefinition GateBesideTerminalDefinition(string gateKey) => new()
    {
        SchemaVersion = 1,
        Nodes = new List<NodeDefinition>
        {
            new() { Id = "start", TypeKey = "trigger.manual", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
            new() { Id = "gate", TypeKey = CancelGateNode.Key, Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.Json($$"""{ "gate": "{{gateKey}}" }""") },
            new() { Id = "end", TypeKey = "builtin.terminal", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
        },
        Edges = new List<EdgeDefinition>
        {
            new() { From = "start", To = "gate" },
            new() { From = "start", To = "end" },
        },
    };

    // start → boom (fails once, no error edge) → gate → end. The first walk fails at boom; the continued walk re-runs it,
    // passes, and parks in the gate — a run that is live again, mid-step.
    private static WorkflowDefinition FlakyThenGatedChainDefinition(string flakyKey, string gateKey) => new()
    {
        SchemaVersion = 1,
        Nodes = new List<NodeDefinition>
        {
            new() { Id = "start", TypeKey = "trigger.manual", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
            new() { Id = "boom", TypeKey = FlakyTestNode.Key, Config = WorkflowsTestSeed.Json($$"""{ "key": "{{flakyKey}}", "failTimes": 1 }"""), Inputs = WorkflowsTestSeed.EmptyJson() },
            new() { Id = "gate", TypeKey = CancelGateNode.Key, Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.Json($$"""{ "gate": "{{gateKey}}" }""") },
            new() { Id = "end", TypeKey = "builtin.terminal", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
        },
        Edges = new List<EdgeDefinition>
        {
            new() { From = "start", To = "boom" },
            new() { From = "boom", To = "gate" },
            new() { From = "gate", To = "end" },
        },
    };

    // start → sub (flow.subworkflow → the gated child) → end. No result ref since a cancelled child produces none.
    private static WorkflowDefinition SubworkflowParentDefinition(Guid childWorkflowId) => new()
    {
        SchemaVersion = 1,
        Nodes = new List<NodeDefinition>
        {
            new() { Id = "start", TypeKey = "trigger.manual", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
            new() { Id = "sub", TypeKey = "flow.subworkflow", Config = WorkflowsTestSeed.Json($$"""{"workflowId":"{{childWorkflowId}}"}"""), Inputs = WorkflowsTestSeed.Json("""{"inputs":{"x":"v"}}""") },
            new() { Id = "end", TypeKey = "builtin.terminal", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
        },
        Edges = new List<EdgeDefinition> { new() { From = "start", To = "sub" }, new() { From = "sub", To = "end" } },
    };

    private static async Task<int> NodeStartedCountAsync(CodeSpaceDbContext db, Guid runId, string nodeId) =>
        await db.WorkflowRunRecord.AsNoTracking()
            .CountAsync(r => r.RunId == runId && r.NodeId == nodeId && r.RecordType == WorkflowRunRecordTypes.NodeStarted);

    // start → gate (blocks the wave) → after (would run next) → end. The chain is linear so `after` is a strictly
    // LATER wave than `gate` — the wave-boundary cancel check fires before it is ever executed.
    private static WorkflowDefinition GatedChainDefinition(string gateKey) => new()
    {
        SchemaVersion = 1,
        Nodes = new List<NodeDefinition>
        {
            new() { Id = "start", TypeKey = "trigger.manual", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
            new() { Id = "gate", TypeKey = CancelGateNode.Key, Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.Json($$"""{ "gate": "{{gateKey}}" }""") },
            new() { Id = "after", TypeKey = JsonEmitNode.Key, Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.Json("""{ "ran": true }""") },
            new() { Id = "end", TypeKey = "builtin.terminal", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
        },
        Edges = new List<EdgeDefinition>
        {
            new() { From = "start", To = "gate" },
            new() { From = "gate", To = "after" },
            new() { From = "after", To = "end" },
        },
    };

    private async Task<Guid> CreateWorkflowAsync(Guid teamId, Guid userId, WorkflowDefinition definition)
    {
        using var scope = _fixture.BeginScopeAs(userId, teamId, Roles.Admin);
        return await scope.Resolve<IMediator>().Send(new CreateWorkflowCommand
        {
            Name = "cancel-" + Guid.NewGuid().ToString("N")[..6],
            Description = null,
            Definition = definition,
            Activations = new List<WorkflowActivationInput>(),
            Enabled = true,
        });
    }
}
