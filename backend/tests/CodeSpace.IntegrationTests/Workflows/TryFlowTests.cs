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
/// Engine v2 — <c>flow.try</c> scope container (region-level try/catch), against real Postgres + the
/// real engine. A try owns a body subgraph (nodes with <c>ParentId == tryId</c>, rooted at a
/// <c>flow.try_start</c>) and runs it ONCE: a clean body routes the run down the default output; an
/// unhandled body failure is CAUGHT — routed down the <c>catch</c> handle (the failure surfaced as the
/// try's <c>error</c> output) instead of failing the run. Pins: success→out, failure→catch (default
/// skipped + error output present), a body node's OWN error edge wins first (try takes the out path),
/// durable suspend inside the body, and nesting (a loop inside a try). Body nodes run under iteration
/// key "&lt;tryId&gt;" so their ledger rows never collide with the surrounding scope's.
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public class TryFlowTests
{
    private readonly PostgresFixture _fixture;

    public TryFlowTests(PostgresFixture fixture) { _fixture = fixture; }

    [Fact]
    public async Task Clean_body_routes_down_the_default_output_and_skips_catch()
    {
        var okKey = "ok-" + Guid.NewGuid().ToString("N");

        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var workflowId = await CreateWorkflowAsync(teamId, userId, TryDefinition(bodyFailKey: null, okKey: okKey));
        var runId = await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, teamId);

        await RunEngineAsync(runId);

        using var db = Db();
        (await Run(db, runId)).Status.ShouldBe(WorkflowRunStatus.Success);
        (await NodeAsync(db, runId, "try")).Status.ShouldBe(NodeStatus.Success);
        (await NodeAsync(db, runId, "ok", "try")).Status.ShouldBe(NodeStatus.Success, "the body node ran under the try body key");
        (await NodeAsync(db, runId, "done")).Status.ShouldBe(NodeStatus.Success, "the default success output ran");
        (await NodeAsync(db, runId, "caught")).Status.ShouldBe(NodeStatus.Skipped, "the catch branch is dead on success");
    }

    [Fact]
    public async Task Unhandled_body_failure_is_caught_and_routes_down_the_catch_handle()
    {
        var boomKey = "boom-" + Guid.NewGuid().ToString("N");

        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var workflowId = await CreateWorkflowAsync(teamId, userId, TryDefinition(bodyFailKey: boomKey, okKey: null));
        var runId = await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, teamId);

        await RunEngineAsync(runId);

        using var db = Db();
        var run = await Run(db, runId);
        run.Status.ShouldBe(WorkflowRunStatus.Success, "the try caught the body failure — the run did NOT fail");
        (await NodeAsync(db, runId, "try")).Status.ShouldBe(NodeStatus.Success);
        (await NodeAsync(db, runId, "boom", "try")).Status.ShouldBe(NodeStatus.Failure, "the body node still failed — it was caught, not un-failed");
        (await NodeAsync(db, runId, "caught")).Status.ShouldBe(NodeStatus.Success, "the catch branch ran");
        (await NodeAsync(db, runId, "done")).Status.ShouldBe(NodeStatus.Skipped, "the default output is dead when the body failed");

        // The catch terminal captured the try's error output → it round-trips to the run outputs.
        JsonDocument.Parse(run.OutputsJson).RootElement.GetProperty("caught").GetString()
            .ShouldNotBeNullOrEmpty("the catch handler read {{nodes.try.outputs.error.message}}");
    }

    [Fact]
    public async Task A_body_nodes_own_error_edge_is_handled_in_body_so_the_try_takes_the_success_path()
    {
        // Phase 2 composes with the try scope: a body node that routes its OWN failure to an in-body
        // handler is "handled" — the body does NOT fail unhandled, so the try takes the default output,
        // not catch. The try only catches OTHERWISE-unhandled failures.
        var boomKey = "boom-" + Guid.NewGuid().ToString("N");

        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var workflowId = await CreateWorkflowAsync(teamId, userId, TryWithInBodyErrorEdgeDefinition(boomKey));
        var runId = await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, teamId);

        await RunEngineAsync(runId);

        using var db = Db();
        (await Run(db, runId)).Status.ShouldBe(WorkflowRunStatus.Success);
        (await NodeAsync(db, runId, "boom", "try")).Status.ShouldBe(NodeStatus.Failure);
        (await NodeAsync(db, runId, "bhandler", "try")).Status.ShouldBe(NodeStatus.Success, "the in-body error edge handled it");
        (await NodeAsync(db, runId, "try")).Status.ShouldBe(NodeStatus.Success);
        (await NodeAsync(db, runId, "done")).Status.ShouldBe(NodeStatus.Success, "body handled its own failure → the try took the default output");
        (await NodeAsync(db, runId, "caught")).Status.ShouldBe(NodeStatus.Skipped);
    }

    [Fact]
    public async Task A_suspend_in_the_try_body_parks_then_resumes_down_the_default_output()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var workflowId = await CreateWorkflowAsync(teamId, userId, TrySuspendingBodyDefinition());
        var runId = await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, teamId);

        await RunEngineAsync(runId);

        using (var parked = Db())
        {
            (await Run(parked, runId)).Status.ShouldBe(WorkflowRunStatus.Suspended, "the try body's approval parked the run");
            var wait = await parked.WorkflowRunWait.AsNoTracking().SingleAsync(w => w.RunId == runId && w.Status == WorkflowWaitStatuses.Pending);
            wait.WaitKind.ShouldBe(WorkflowWaitKinds.Approval);
            wait.IterationKey.ShouldBe("try", "the body node suspended under the try body key");
        }

        (await ApproveAsync(runId, teamId, userId)).ShouldBeTrue();
        await RunEngineAsync(runId);

        using var db = Db();
        (await Run(db, runId)).Status.ShouldBe(WorkflowRunStatus.Success);
        (await NodeAsync(db, runId, "try")).Status.ShouldBe(NodeStatus.Success);
        (await NodeAsync(db, runId, "done")).Status.ShouldBe(NodeStatus.Success);
    }

    [Fact]
    public async Task A_loop_nested_inside_a_try_runs_to_completion_and_the_try_succeeds()
    {
        var probeKey = "probe-" + Guid.NewGuid().ToString("N");
        LoopProbeNode.Reset(probeKey);

        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var workflowId = await CreateWorkflowAsync(teamId, userId, TryWithNestedLoopDefinition(probeKey));
        var runId = await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, teamId);

        await RunEngineAsync(runId);

        using var db = Db();
        (await Run(db, runId)).Status.ShouldBe(WorkflowRunStatus.Success);
        (await NodeAsync(db, runId, "try")).Status.ShouldBe(NodeStatus.Success);
        // The inner loop runs under the try body key prefix: "try/loop#<i>".
        var loop = await db.WorkflowRunNode.AsNoTracking().SingleAsync(n => n.RunId == runId && n.NodeId == "loop" && n.IterationKey == "try");
        loop.Status.ShouldBe(NodeStatus.Success);
        JsonDocument.Parse(loop.OutputsJson).RootElement.GetProperty("iterations").GetInt32().ShouldBe(2);
        LoopProbeNode.SeenFor(probeKey).Count.ShouldBe(2, "the loop body ran twice inside the try");
    }

    // ─── Helpers ────────────────────────────────────────────────────────────────

    private CodeSpaceDbContext Db() => _fixture.BeginScope().Resolve<CodeSpaceDbContext>();

    private static async Task<Core.Persistence.Entities.WorkflowRun> Run(CodeSpaceDbContext db, Guid runId) =>
        await db.WorkflowRun.AsNoTracking().SingleAsync(r => r.Id == runId);

    private static async Task<Core.Persistence.Entities.WorkflowRunNode> NodeAsync(CodeSpaceDbContext db, Guid runId, string nodeId, string iterationKey = "") =>
        await db.WorkflowRunNode.AsNoTracking().SingleAsync(n => n.RunId == runId && n.NodeId == nodeId && n.IterationKey == iterationKey);

    private async Task<Guid> CreateWorkflowAsync(Guid teamId, Guid userId, WorkflowDefinition definition)
    {
        using var scope = _fixture.BeginScopeAs(userId, teamId, Roles.Admin);
        return await scope.Resolve<IMediator>().Send(new CreateWorkflowCommand
        {
            Name = "try-" + Guid.NewGuid().ToString("N")[..6], Description = null,
            Definition = definition, Activations = new List<WorkflowActivationInput>(), Enabled = true,
        });
    }

    private async Task RunEngineAsync(Guid runId)
    {
        using var scope = _fixture.BeginScope();
        await scope.Resolve<IWorkflowEngine>().ExecuteRunAsync(runId, CancellationToken.None);
    }

    private async Task<bool> ApproveAsync(Guid runId, Guid teamId, Guid userId)
    {
        using var scope = _fixture.BeginScopeAs(userId, teamId, Roles.Admin);
        return await scope.Resolve<IMediator>().Send(new ResumeRunCommand { RunId = runId, Approved = true, Comment = "ok" });
    }

    private static NodeDefinition Flaky(string id, string? parentId, string key, int failTimes) => new()
    {
        Id = id, TypeKey = FlakyTestNode.Key, ParentId = parentId, Inputs = WorkflowsTestSeed.EmptyJson(),
        Config = WorkflowsTestSeed.Json("""{ "key": "__KEY__", "failTimes": __FT__ }""".Replace("__KEY__", key).Replace("__FT__", failTimes.ToString())),
    };

    // manual → try(body: try_start → [ok | boom]) → done; try =(catch)=> caught. Exactly one of okKey /
    // bodyFailKey is set: okKey ⇒ a succeeding body node; bodyFailKey ⇒ an always-failing one.
    private static WorkflowDefinition TryDefinition(string? bodyFailKey, string? okKey)
    {
        var bodyNode = bodyFailKey != null
            ? Flaky("boom", "try", bodyFailKey, 99)
            : Flaky("ok", "try", okKey!, 0);

        return new WorkflowDefinition
        {
            SchemaVersion = 1,
            Nodes = new List<NodeDefinition>
            {
                new() { Id = "start", TypeKey = "trigger.manual", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
                new() { Id = "try", TypeKey = "flow.try", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
                new() { Id = "ts", TypeKey = "flow.try_start", ParentId = "try", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
                bodyNode,
                new() { Id = "done", TypeKey = "builtin.terminal", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
                new() { Id = "caught", TypeKey = "builtin.terminal", Config = WorkflowsTestSeed.EmptyJson(),
                        Inputs = WorkflowsTestSeed.Json("""{ "caught": "{{nodes.try.outputs.error.message}}" }""") },
            },
            Edges = new List<EdgeDefinition>
            {
                new() { From = "start", To = "try" },
                new() { From = "ts", To = bodyFailKey != null ? "boom" : "ok" },
                new() { From = "try", To = "done" },
                new() { From = "try", To = "caught", SourceHandle = WorkflowHandles.Catch },
            },
        };
    }

    // manual → try(body: try_start → boom[fails] =(error)=> bhandler) → done; try =(catch)=> caught.
    private static WorkflowDefinition TryWithInBodyErrorEdgeDefinition(string boomKey) => new()
    {
        SchemaVersion = 1,
        Nodes = new List<NodeDefinition>
        {
            new() { Id = "start", TypeKey = "trigger.manual", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
            new() { Id = "try", TypeKey = "flow.try", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
            new() { Id = "ts", TypeKey = "flow.try_start", ParentId = "try", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
            Flaky("boom", "try", boomKey, 99),
            Flaky("bhandler", "try", boomKey + "-h", 0),
            new() { Id = "done", TypeKey = "builtin.terminal", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
            new() { Id = "caught", TypeKey = "builtin.terminal", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
        },
        Edges = new List<EdgeDefinition>
        {
            new() { From = "start", To = "try" },
            new() { From = "ts", To = "boom" },
            new() { From = "boom", To = "bhandler", SourceHandle = WorkflowHandles.Error },
            new() { From = "try", To = "done" },
            new() { From = "try", To = "caught", SourceHandle = WorkflowHandles.Catch },
        },
    };

    // manual → try(body: try_start → gate[wait_approval]) → done; try =(catch)=> caught.
    private static WorkflowDefinition TrySuspendingBodyDefinition() => new()
    {
        SchemaVersion = 1,
        Nodes = new List<NodeDefinition>
        {
            new() { Id = "start", TypeKey = "trigger.manual", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
            new() { Id = "try", TypeKey = "flow.try", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
            new() { Id = "ts", TypeKey = "flow.try_start", ParentId = "try", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
            new() { Id = "gate", TypeKey = "flow.wait_approval", ParentId = "try", Config = WorkflowsTestSeed.Json("""{ "prompt": "go?" }"""), Inputs = WorkflowsTestSeed.EmptyJson() },
            new() { Id = "done", TypeKey = "builtin.terminal", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
            new() { Id = "caught", TypeKey = "builtin.terminal", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
        },
        Edges = new List<EdgeDefinition>
        {
            new() { From = "start", To = "try" },
            new() { From = "ts", To = "gate" },
            new() { From = "try", To = "done" },
            new() { From = "try", To = "caught", SourceHandle = WorkflowHandles.Catch },
        },
    };

    // manual → try(body: try_start → loop(2 passes; loop_start → probe)) → done; try =(catch)=> caught.
    private static WorkflowDefinition TryWithNestedLoopDefinition(string probeKey)
    {
        var probeInputs = """{ "key": "__KEY__", "value": "i{{loop.index}}" }""".Replace("__KEY__", probeKey);
        return new WorkflowDefinition
        {
            SchemaVersion = 1,
            Nodes = new List<NodeDefinition>
            {
                new() { Id = "start", TypeKey = "trigger.manual", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
                new() { Id = "try", TypeKey = "flow.try", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
                new() { Id = "ts", TypeKey = "flow.try_start", ParentId = "try", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
                new() { Id = "loop", TypeKey = "flow.loop", ParentId = "try", Inputs = WorkflowsTestSeed.EmptyJson(), Config = WorkflowsTestSeed.Json("""{ "maxIterations": 2 }""") },
                new() { Id = "ls", TypeKey = "flow.loop_start", ParentId = "loop", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
                new() { Id = "probe", TypeKey = LoopProbeNode.Key, ParentId = "loop", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.Json(probeInputs) },
                new() { Id = "done", TypeKey = "builtin.terminal", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
                new() { Id = "caught", TypeKey = "builtin.terminal", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
            },
            Edges = new List<EdgeDefinition>
            {
                new() { From = "start", To = "try" },
                new() { From = "ts", To = "loop" },
                new() { From = "ls", To = "probe" },
                new() { From = "try", To = "done" },
                new() { From = "try", To = "caught", SourceHandle = WorkflowHandles.Catch },
            },
        };
    }
}
