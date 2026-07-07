using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Services.Workflows;
using CodeSpace.Core.Services.Workflows.Engine;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.IntegrationTests.Workflows.Infrastructure;
using CodeSpace.Messages.Commands.Workflows;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Dtos.Workflows;
using CodeSpace.Messages.Enums;
using MediatR;
using Microsoft.EntityFrameworkCore;
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
