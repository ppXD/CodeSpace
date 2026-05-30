using System.Text.Json;
using Autofac;
using CodeSpace.Core.Persistence.Db;
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
/// Engine v2 Phase 3 — <c>flow.loop</c>, against real Postgres + the real engine. A loop container
/// owns a body subgraph (nodes whose ParentId is the loop, rooted at a flow.loop_start) and re-runs
/// it once per iteration until a termination condition is met or the iteration cap is hit; loop
/// variables thread state across passes. Pins: condition-met exit + variable threading + the seen
/// sequence; the max-iterations cap; a body failure (no error edge) failing the loop; an error edge
/// INSIDE the body being handled; and a suspending body node being refused with a clear message.
/// Each pass persists its body nodes under iteration key "&lt;loopId&gt;#&lt;i&gt;".
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public class LoopFlowTests
{
    private readonly PostgresFixture _fixture;

    public LoopFlowTests(PostgresFixture fixture) { _fixture = fixture; }

    [Fact]
    public async Task Loop_runs_until_the_condition_and_threads_a_variable()
    {
        var key = "probe-" + Guid.NewGuid().ToString("N");
        LoopProbeNode.Reset(key);

        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        // acc starts "start"; each pass appends ":<index>". Terminate when loop.index eq "2".
        var workflowId = await CreateWorkflowAsync(teamId, userId, ConditionLoopDefinition(key, terminateAtIndex: "2", maxIterations: 10));
        var runId = await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, teamId);

        await RunEngineAsync(runId);

        using var verify = _fixture.BeginScope();
        var db = verify.Resolve<CodeSpaceDbContext>();

        (await db.WorkflowRun.AsNoTracking().SingleAsync(r => r.Id == runId)).Status.ShouldBe(WorkflowRunStatus.Success);

        var loop = await LoopNodeAsync(db, runId);
        loop.Status.ShouldBe(NodeStatus.Success);

        var outputs = JsonDocument.Parse(loop.OutputsJson).RootElement;
        outputs.GetProperty("iterations").GetInt32().ShouldBe(3, "i=0,1,2 — the check at the end of pass 2 sees index==2");
        outputs.GetProperty("terminationReason").GetString().ShouldBe("condition");
        outputs.GetProperty("acc").GetString().ShouldBe("start:0:1:2", "the update ref threaded acc across every pass");

        // The body saw the PREVIOUS pass's accumulated value each iteration — proves real threading.
        LoopProbeNode.SeenFor(key).ShouldBe(new[] { "start", "start:0", "start:0:1" });

        // Each pass persisted the body probe under its own iteration key.
        var probeKeys = await db.WorkflowRunNode.AsNoTracking()
            .Where(n => n.RunId == runId && n.NodeId == "probe").Select(n => n.IterationKey).ToListAsync();
        probeKeys.OrderBy(k => k).ShouldBe(new[] { "loop#0", "loop#1", "loop#2" });
    }

    [Fact]
    public async Task Loop_stops_at_the_max_iterations_cap()
    {
        var key = "probe-" + Guid.NewGuid().ToString("N");
        LoopProbeNode.Reset(key);

        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        // Condition never met (index never reaches 99); the cap of 3 stops it.
        var workflowId = await CreateWorkflowAsync(teamId, userId, ConditionLoopDefinition(key, terminateAtIndex: "99", maxIterations: 3));
        var runId = await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, teamId);

        await RunEngineAsync(runId);

        using var verify = _fixture.BeginScope();
        var db = verify.Resolve<CodeSpaceDbContext>();

        (await db.WorkflowRun.AsNoTracking().SingleAsync(r => r.Id == runId)).Status.ShouldBe(WorkflowRunStatus.Success);

        var outputs = JsonDocument.Parse((await LoopNodeAsync(db, runId)).OutputsJson).RootElement;
        outputs.GetProperty("iterations").GetInt32().ShouldBe(3);
        outputs.GetProperty("terminationReason").GetString().ShouldBe("maxIterations");
        LoopProbeNode.SeenFor(key).Count.ShouldBe(3, "the cap bounds the body to exactly maxIterations passes");
    }

    [Fact]
    public async Task Body_failure_with_no_error_edge_fails_the_loop_and_run()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var workflowId = await CreateWorkflowAsync(teamId, userId, FailingBodyDefinition(Guid.NewGuid().ToString("N"), withErrorBranch: false));
        var runId = await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, teamId);

        await RunEngineAsync(runId);

        using var verify = _fixture.BeginScope();
        var db = verify.Resolve<CodeSpaceDbContext>();

        (await db.WorkflowRun.AsNoTracking().SingleAsync(r => r.Id == runId)).Status
            .ShouldBe(WorkflowRunStatus.Failure, "an unhandled body failure fails the loop, which fails the run");
        (await LoopNodeAsync(db, runId)).Status.ShouldBe(NodeStatus.Failure);
    }

    [Fact]
    public async Task Error_edge_inside_the_body_is_handled_and_the_loop_succeeds()
    {
        // Phase 2 + Phase 3 compose: a failing body node routes down its `error` edge to a handler
        // INSIDE the loop body, so the iteration succeeds and the loop completes normally.
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var workflowId = await CreateWorkflowAsync(teamId, userId, FailingBodyDefinition(Guid.NewGuid().ToString("N"), withErrorBranch: true));
        var runId = await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, teamId);

        await RunEngineAsync(runId);

        using var verify = _fixture.BeginScope();
        var db = verify.Resolve<CodeSpaceDbContext>();

        (await db.WorkflowRun.AsNoTracking().SingleAsync(r => r.Id == runId)).Status.ShouldBe(WorkflowRunStatus.Success);
        (await LoopNodeAsync(db, runId)).Status.ShouldBe(NodeStatus.Success);
        // The in-body error handler ran on the first iteration.
        (await db.WorkflowRunNode.AsNoTracking().AnyAsync(n => n.RunId == runId && n.NodeId == "caught" && n.Status == NodeStatus.Success))
            .ShouldBeTrue("the body's error-branch handler ran");
    }

    [Fact]
    public async Task Suspending_node_in_a_loop_body_fails_with_a_clear_message()
    {
        // Durable suspend-in-loop is a follow-up; for now a suspending body node must fail loudly,
        // never silently park or mis-resume.
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var workflowId = await CreateWorkflowAsync(teamId, userId, SuspendingBodyDefinition());
        var runId = await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, teamId);

        await RunEngineAsync(runId);

        using var verify = _fixture.BeginScope();
        var db = verify.Resolve<CodeSpaceDbContext>();

        (await db.WorkflowRun.AsNoTracking().SingleAsync(r => r.Id == runId)).Status.ShouldBe(WorkflowRunStatus.Failure);
        var loop = await LoopNodeAsync(db, runId);
        loop.Status.ShouldBe(NodeStatus.Failure);
        loop.Error.ShouldNotBeNull();
        loop.Error!.ShouldContain("suspend");
    }

    // ─── Helpers ────────────────────────────────────────────────────────────────

    private static async Task<Core.Persistence.Entities.WorkflowRunNode> LoopNodeAsync(CodeSpaceDbContext db, Guid runId) =>
        await db.WorkflowRunNode.AsNoTracking().SingleAsync(n => n.RunId == runId && n.NodeId == "loop" && n.IterationKey == "");

    private async Task<Guid> CreateWorkflowAsync(Guid teamId, Guid userId, WorkflowDefinition definition)
    {
        using var scope = _fixture.BeginScopeAs(userId, teamId, Roles.Admin);
        return await scope.Resolve<IMediator>().Send(new CreateWorkflowCommand
        {
            Name = "loop-" + Guid.NewGuid().ToString("N")[..6],
            Description = null,
            Definition = definition,
            Activations = new List<WorkflowActivationInput>(),
            Enabled = true,
        });
    }

    private async Task RunEngineAsync(Guid runId)
    {
        using var scope = _fixture.BeginScope();
        await scope.Resolve<IWorkflowEngine>().ExecuteRunAsync(runId, CancellationToken.None);
    }

    // manual → loop(body: loop_start → probe) → terminal. acc threads "start" + ":<index>" each pass.
    // Plain (non-interpolated) raw strings keep the literal {{loop.*}} / {{nodes.*}} templates intact;
    // the two scalar params are spliced via Replace (a $$"""…""" string would mis-read {{loop.acc}}).
    private static WorkflowDefinition ConditionLoopDefinition(string probeKey, string terminateAtIndex, int maxIterations)
    {
        var loopConfig = """
            {
              "loopVariables": [ { "name": "acc", "type": "String", "value": "start", "update": "{{loop.acc}}:{{loop.index}}" } ],
              "termination": { "logic": "and", "conditions": [ { "ref": "{{loop.index}}", "op": "eq", "value": "__IDX__" } ] },
              "maxIterations": __MAX__
            }
            """.Replace("__IDX__", terminateAtIndex).Replace("__MAX__", maxIterations.ToString());

        var probeInputs = """{ "key": "__KEY__", "value": "{{loop.acc}}" }""".Replace("__KEY__", probeKey);

        return new WorkflowDefinition
        {
            SchemaVersion = 1,
            Nodes = new List<NodeDefinition>
            {
                new() { Id = "start", TypeKey = "trigger.manual", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
                new() { Id = "loop", TypeKey = "flow.loop", Config = WorkflowsTestSeed.Json(loopConfig), Inputs = WorkflowsTestSeed.EmptyJson() },
                new() { Id = "ls", TypeKey = "flow.loop_start", ParentId = "loop", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
                new() { Id = "probe", TypeKey = LoopProbeNode.Key, ParentId = "loop",
                        Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.Json(probeInputs) },
                new() { Id = "end", TypeKey = "builtin.terminal", Config = WorkflowsTestSeed.EmptyJson(),
                        Inputs = WorkflowsTestSeed.Json("""{ "iters": "{{nodes.loop.outputs.iterations}}", "acc": "{{nodes.loop.outputs.acc}}" }""") },
            },
            Edges = new List<EdgeDefinition>
            {
                new() { From = "start", To = "loop" },
                new() { From = "loop", To = "end" },
                new() { From = "ls", To = "probe" },
            },
        };
    }

    // manual → loop(body: loop_start → boom[always fails]; optionally boom =(error)=> caught) → terminal.
    private static WorkflowDefinition FailingBodyDefinition(string flakyKey, bool withErrorBranch)
    {
        var nodes = new List<NodeDefinition>
        {
            new() { Id = "start", TypeKey = "trigger.manual", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
            new() { Id = "loop", TypeKey = "flow.loop", Inputs = WorkflowsTestSeed.EmptyJson(),
                    Config = WorkflowsTestSeed.Json("""{ "termination": { "conditions": [ { "ref": "{{loop.index}}", "op": "eq", "value": "0" } ] }, "maxIterations": 3 }""") },
            new() { Id = "ls", TypeKey = "flow.loop_start", ParentId = "loop", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
            new() { Id = "boom", TypeKey = FlakyTestNode.Key, ParentId = "loop",
                    Config = WorkflowsTestSeed.Json($$"""{ "key": "{{flakyKey}}", "failTimes": 99 }"""), Inputs = WorkflowsTestSeed.EmptyJson() },
            new() { Id = "end", TypeKey = "builtin.terminal", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
        };

        var edges = new List<EdgeDefinition>
        {
            new() { From = "start", To = "loop" },
            new() { From = "loop", To = "end" },
            new() { From = "ls", To = "boom" },
        };

        if (withErrorBranch)
        {
            // An always-succeeds handler (FlakyTestNode with failTimes:0) wired to boom's error edge.
            nodes.Add(new() { Id = "caught", TypeKey = FlakyTestNode.Key, ParentId = "loop",
                              Config = WorkflowsTestSeed.Json($$"""{ "key": "{{flakyKey}}-caught", "failTimes": 0 }"""), Inputs = WorkflowsTestSeed.EmptyJson() });
            edges.Add(new() { From = "boom", To = "caught", SourceHandle = WorkflowHandles.Error });
        }

        return new WorkflowDefinition { SchemaVersion = 1, Nodes = nodes, Edges = edges };
    }

    // manual → loop(body: loop_start → sleep[suspends]) → terminal.
    private static WorkflowDefinition SuspendingBodyDefinition() => new()
    {
        SchemaVersion = 1,
        Nodes = new List<NodeDefinition>
        {
            new() { Id = "start", TypeKey = "trigger.manual", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
            new() { Id = "loop", TypeKey = "flow.loop", Inputs = WorkflowsTestSeed.EmptyJson(),
                    Config = WorkflowsTestSeed.Json("""{ "termination": { "conditions": [ { "ref": "{{loop.index}}", "op": "eq", "value": "0" } ] }, "maxIterations": 3 }""") },
            new() { Id = "ls", TypeKey = "flow.loop_start", ParentId = "loop", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
            new() { Id = "nap", TypeKey = "flow.sleep", ParentId = "loop",
                    Config = WorkflowsTestSeed.Json("""{ "seconds": 60 }"""), Inputs = WorkflowsTestSeed.EmptyJson() },
            new() { Id = "end", TypeKey = "builtin.terminal", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
        },
        Edges = new List<EdgeDefinition>
        {
            new() { From = "start", To = "loop" },
            new() { From = "loop", To = "end" },
            new() { From = "ls", To = "nap" },
        },
    };
}
