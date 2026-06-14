using System.Text.Json;
using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
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
/// Track E — <c>flow.map</c>, against real Postgres + the real engine. A map container binds a collection
/// via its <c>items</c> INPUT and fans the body subgraph (nodes whose ParentId is the map, rooted at a
/// flow.map_start) out into N PARALLEL element-branches; each branch sees its element as {{item}} /
/// {{index}}; each branch's terminal output reduces — in element order — into a keyed array a downstream
/// step reads. Pins: a static fan-out (3 → results[3] ordered); the PLANNER bridge (a stub emitting
/// json.subtasks → map → results readable downstream as results / results[0] / .length); empty array →
/// empty results no-op; non-array → clean Fail; per-element {{item}}/{{index}} scope; terminate vs
/// continue-on-error; nested-map depth guard; bounded parallelism (maxParallelism 1 vs N, both correct).
/// These are the SYNCHRONOUS-body cases (the PR1 surface, still exactly correct under PR2). The durable
/// parallel-branch suspend/resume cases (a body node that PARKS) live in <c>MapDurableResumeFlowTests</c>.
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public class MapFlowTests
{
    private readonly PostgresFixture _fixture;

    public MapFlowTests(PostgresFixture fixture) { _fixture = fixture; }

    [Fact]
    public async Task Static_array_fans_out_into_one_branch_per_element_with_ordered_results()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        // trigger carries a 3-element array; the map fans it out, each branch echoes its {{item}}.
        var workflowId = await CreateWorkflowAsync(teamId, userId, StaticArrayMapDefinition());
        var runId = await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, teamId, payloadJson: """{ "things": ["a", "b", "c"] }""");

        await RunEngineAsync(runId);

        using var verify = _fixture.BeginScope();
        var db = verify.Resolve<CodeSpaceDbContext>();
        (await db.WorkflowRun.AsNoTracking().SingleAsync(r => r.Id == runId)).Status.ShouldBe(WorkflowRunStatus.Success);

        var map = await MapNodeAsync(db, runId);
        map.Status.ShouldBe(NodeStatus.Success);

        var outputs = JsonDocument.Parse(map.OutputsJson).RootElement;
        outputs.GetProperty("count").GetInt32().ShouldBe(3);
        outputs.GetProperty("failed").GetInt32().ShouldBe(0);

        // results[i] = the branch terminal's output { value: <element_i> }, ordered by element index.
        var results = outputs.GetProperty("results");
        results.GetArrayLength().ShouldBe(3);
        ResultValue(results, 0).ShouldBe("a");
        ResultValue(results, 1).ShouldBe("b");
        ResultValue(results, 2).ShouldBe("c");

        // Each element ran under its own iteration key "<mapId>#<i>".
        var branchKeys = await db.WorkflowRunNode.AsNoTracking()
            .Where(n => n.RunId == runId && n.NodeId == "work").Select(n => n.IterationKey).ToListAsync();
        branchKeys.OrderBy(k => k).ShouldBe(new[] { "map#0", "map#1", "map#2" });
    }

    [Fact]
    public async Task Planner_bridge_fans_a_dynamic_subtasks_array_and_is_readable_downstream()
    {
        // The headline planner+parallel-subagents shape: a stub "planner" emits json.subtasks (an array);
        // flow.map(items={{nodes.planner.outputs.json.subtasks}}) fans over it; a downstream terminal
        // reads {{nodes.map.outputs.results}} / results[0] / results.length.
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var workflowId = await CreateWorkflowAsync(teamId, userId, PlannerBridgeMapDefinition());
        var runId = await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, teamId);

        await RunEngineAsync(runId);

        using var verify = _fixture.BeginScope();
        var db = verify.Resolve<CodeSpaceDbContext>();
        var run = await db.WorkflowRun.AsNoTracking().SingleAsync(r => r.Id == runId);
        run.Status.ShouldBe(WorkflowRunStatus.Success);

        var map = await MapNodeAsync(db, runId);
        JsonDocument.Parse(map.OutputsJson).RootElement.GetProperty("count").GetInt32().ShouldBe(2);

        // The downstream synthesizer captured results / results[0].value / results.length as the run's outputs.
        var runOutputs = JsonDocument.Parse(run.OutputsJson).RootElement;
        runOutputs.GetProperty("length").GetInt32().ShouldBe(2, "{{...results.length}} resolved against the reduced array");
        runOutputs.GetProperty("first").GetString().ShouldBe("plan-x", "{{...results[0].value}} read the first element's branch output");
        runOutputs.GetProperty("all").GetArrayLength().ShouldBe(2, "{{...results}} resolved to the whole array");
    }

    [Fact]
    public async Task Empty_array_is_a_no_op_that_succeeds_with_empty_results()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var workflowId = await CreateWorkflowAsync(teamId, userId, StaticArrayMapDefinition());
        var runId = await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, teamId, payloadJson: """{ "things": [] }""");

        await RunEngineAsync(runId);

        using var verify = _fixture.BeginScope();
        var db = verify.Resolve<CodeSpaceDbContext>();
        (await db.WorkflowRun.AsNoTracking().SingleAsync(r => r.Id == runId)).Status.ShouldBe(WorkflowRunStatus.Success, "an empty collection is a valid no-op, not an error");

        var outputs = JsonDocument.Parse((await MapNodeAsync(db, runId)).OutputsJson).RootElement;
        outputs.GetProperty("count").GetInt32().ShouldBe(0);
        outputs.GetProperty("results").GetArrayLength().ShouldBe(0);

        // Zero branches ran — no body node persisted under any "map#<i>" key.
        (await db.WorkflowRunNode.AsNoTracking().CountAsync(n => n.RunId == runId && n.NodeId == "work")).ShouldBe(0);
    }

    [Fact]
    public async Task Non_array_items_fails_the_map_cleanly()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var workflowId = await CreateWorkflowAsync(teamId, userId, StaticArrayMapDefinition());
        // 'things' is an object, not an array.
        var runId = await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, teamId, payloadJson: """{ "things": { "not": "an array" } }""");

        await RunEngineAsync(runId);

        using var verify = _fixture.BeginScope();
        var db = verify.Resolve<CodeSpaceDbContext>();
        var run = await db.WorkflowRun.AsNoTracking().SingleAsync(r => r.Id == runId);
        run.Status.ShouldBe(WorkflowRunStatus.Failure);

        // The map node failed with the descriptive non-array message (the run-level Error is the walker's
        // generic "Node '<id>' failed." since the map has no error edge — same as flow.loop).
        var map = await MapNodeAsync(db, runId);
        map.Status.ShouldBe(NodeStatus.Failure);
        map.Error.ShouldContain("must be an array");
    }

    [Fact]
    public async Task Each_branch_sees_its_own_item_and_index()
    {
        // The per-element body echoes BOTH {{item}} and {{index}} so we can prove the Iteration scope is
        // distinct per branch and the index matches the element position.
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var workflowId = await CreateWorkflowAsync(teamId, userId, ItemAndIndexMapDefinition());
        var runId = await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, teamId, payloadJson: """{ "things": ["x", "y", "z"] }""");

        await RunEngineAsync(runId);

        using var verify = _fixture.BeginScope();
        var db = verify.Resolve<CodeSpaceDbContext>();
        (await db.WorkflowRun.AsNoTracking().SingleAsync(r => r.Id == runId)).Status.ShouldBe(WorkflowRunStatus.Success);

        var results = JsonDocument.Parse((await MapNodeAsync(db, runId)).OutputsJson).RootElement.GetProperty("results");
        for (var i = 0; i < 3; i++)
        {
            results[i].GetProperty("item").GetString().ShouldBe(new[] { "x", "y", "z" }[i]);
            results[i].GetProperty("index").GetInt32().ShouldBe(i);
        }
    }

    [Fact]
    public async Task Continue_on_error_records_a_failure_marker_and_the_map_survives()
    {
        // errorHandling=continue: the element whose value is "boom" makes its branch terminal fail (a
        // FlakyTestNode that always fails); that element's result is a failure marker + failed counts it,
        // the other branches succeed, and the map (and run) succeed.
        var boomKey = "boom-" + Guid.NewGuid().ToString("N");
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var workflowId = await CreateWorkflowAsync(teamId, userId, ContinueOnErrorMapDefinition(boomKey, "continue"));
        var runId = await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, teamId, payloadJson: """{ "things": ["ok1", "ok2"] }""");

        await RunEngineAsync(runId);

        using var verify = _fixture.BeginScope();
        var db = verify.Resolve<CodeSpaceDbContext>();
        (await db.WorkflowRun.AsNoTracking().SingleAsync(r => r.Id == runId)).Status
            .ShouldBe(WorkflowRunStatus.Success, "continue-on-error keeps the map alive despite a failing branch");

        var outputs = JsonDocument.Parse((await MapNodeAsync(db, runId)).OutputsJson).RootElement;
        outputs.GetProperty("count").GetInt32().ShouldBe(2);
        outputs.GetProperty("failed").GetInt32().ShouldBe(2, "both branches go through the always-fail node under continue");

        // Each element's result is a failure marker (an `error` object), not a normal output.
        var results = outputs.GetProperty("results");
        results[0].TryGetProperty("error", out _).ShouldBeTrue("a failed branch's result is a failure marker");
        results[1].TryGetProperty("error", out _).ShouldBeTrue();
    }

    [Fact]
    public async Task Terminate_on_error_fails_the_whole_map()
    {
        // Same shape, default terminate policy: the first failing branch fails the map → the run fails.
        var boomKey = "boom-" + Guid.NewGuid().ToString("N");
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var workflowId = await CreateWorkflowAsync(teamId, userId, ContinueOnErrorMapDefinition(boomKey, errorHandling: null));
        var runId = await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, teamId, payloadJson: """{ "things": ["ok1", "ok2"] }""");

        await RunEngineAsync(runId);

        using var verify = _fixture.BeginScope();
        var db = verify.Resolve<CodeSpaceDbContext>();
        (await db.WorkflowRun.AsNoTracking().SingleAsync(r => r.Id == runId)).Status
            .ShouldBe(WorkflowRunStatus.Failure, "terminate-on-error fails the map when a branch fails");
        (await MapNodeAsync(db, runId)).Status.ShouldBe(NodeStatus.Failure);
    }

    [Fact]
    public async Task A_map_body_that_suspends_validates_at_save()
    {
        // PR2 lifts the PR1 fail-closed guard: a body node that PARKS the run (flow.wait_approval) now VALIDATES
        // at the real operator path. The durable parallel-branch suspend/resume behaviour itself is exercised in
        // MapDurableResumeFlowTests — this is the save-time backstop that the guard was truly removed.
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);

        // Should NOT throw — the create succeeds and returns a workflow id.
        var workflowId = await CreateWorkflowAsync(teamId, userId, SuspendingBodyMapDefinition(errorHandling: null));
        workflowId.ShouldNotBe(Guid.Empty);
    }

    [Fact]
    public async Task A_nested_map_within_the_depth_guard_runs_the_cross_product()
    {
        // Two maps deep (within the cap): the outer fans the trigger array, each branch re-fans its element
        // (a single-element inner array), proving nested maps compose + key collision-free across branches.
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var workflowId = await CreateWorkflowAsync(teamId, userId, DeeplyNestedMapDefinition(depth: 2));
        var runId = await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, teamId, payloadJson: """{ "things": [["a"], ["b"]] }""");

        await RunEngineAsync(runId);

        using var verify = _fixture.BeginScope();
        var db = verify.Resolve<CodeSpaceDbContext>();
        (await db.WorkflowRun.AsNoTracking().SingleAsync(r => r.Id == runId)).Status.ShouldBe(WorkflowRunStatus.Success);

        // The inner-map body nodes ran under nested keys "map_0#<i>/map_1#<j>".
        var innerKeys = await db.WorkflowRunNode.AsNoTracking()
            .Where(n => n.RunId == runId && n.NodeId == "echo_1").Select(n => n.IterationKey).ToListAsync();
        innerKeys.ShouldAllBe(k => k.Contains("/map_1#"));
        innerKeys.Count.ShouldBe(2, "one inner branch per outer element (each inner array has one element)");
    }

    [Fact]
    public async Task A_map_nested_past_the_depth_guard_is_rejected_at_save()
    {
        // 9 maps deep (cap is 8) — the save-time container nesting validator rejects it before any run,
        // the real path an operator hits. (The engine carries the same guard as defense-in-depth.)
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);

        var ex = await Should.ThrowAsync<WorkflowValidationException>(() =>
            CreateWorkflowAsync(teamId, userId, DeeplyNestedMapDefinition(depth: 9)));

        ex.Errors.ShouldContain(e => e.Contains("nested deeper than"));
    }

    [Theory]
    [InlineData(1)]   // strictly sequential branches
    [InlineData(4)]   // up to 4 branches at once
    public async Task Bounded_parallelism_produces_correct_ordered_results(int maxParallelism)
    {
        // The reduce must be correct + ordered regardless of how many branches run at once — a cap of 1
        // (sequential) and a cap of 4 (parallel) both yield results[0..3] in element order.
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var workflowId = await CreateWorkflowAsync(teamId, userId, StaticArrayMapDefinition(maxParallelism));
        var runId = await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, teamId, payloadJson: """{ "things": ["a", "b", "c", "d"] }""");

        await RunEngineAsync(runId);

        using var verify = _fixture.BeginScope();
        var db = verify.Resolve<CodeSpaceDbContext>();
        (await db.WorkflowRun.AsNoTracking().SingleAsync(r => r.Id == runId)).Status.ShouldBe(WorkflowRunStatus.Success);

        var results = JsonDocument.Parse((await MapNodeAsync(db, runId)).OutputsJson).RootElement.GetProperty("results");
        results.GetArrayLength().ShouldBe(4);
        new[] { "a", "b", "c", "d" }.Select((_, i) => ResultValue(results, i)).ShouldBe(new[] { "a", "b", "c", "d" });
    }

    // ─── Helpers ────────────────────────────────────────────────────────────────

    private static async Task<Core.Persistence.Entities.WorkflowRunNode> MapNodeAsync(CodeSpaceDbContext db, Guid runId) =>
        await db.WorkflowRunNode.AsNoTracking().SingleAsync(n => n.RunId == runId && n.NodeId == "map" && n.IterationKey == "");

    private static string ResultValue(JsonElement results, int index) => results[index].GetProperty("value").GetString()!;

    private async Task<Guid> CreateWorkflowAsync(Guid teamId, Guid userId, WorkflowDefinition definition)
    {
        using var scope = _fixture.BeginScopeAs(userId, teamId, Roles.Admin);
        return await scope.Resolve<IMediator>().Send(new CreateWorkflowCommand
        {
            Name = "map-" + Guid.NewGuid().ToString("N")[..6],
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

    // manual → map(items={{trigger.things}}; body: ms → work[value={{item}}] → leaf[value={{item}}]) → terminal.
    // The body terminal `leaf` echoes {{item}} as { value: <item> }, which becomes results[i].
    private static WorkflowDefinition StaticArrayMapDefinition(int? maxParallelism = null)
    {
        var mapConfig = maxParallelism is { } mp ? $$"""{ "maxParallelism": {{mp}} }""" : "{}";
        return new WorkflowDefinition
        {
            SchemaVersion = 1,
            Nodes = new List<NodeDefinition>
            {
                new() { Id = "start", TypeKey = "trigger.manual", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
                new() { Id = "map", TypeKey = "flow.map", Config = WorkflowsTestSeed.Json(mapConfig),
                        Inputs = WorkflowsTestSeed.Json("""{ "items": "{{trigger.things}}" }""") },
                new() { Id = "ms", TypeKey = "flow.map_start", ParentId = "map", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
                new() { Id = "work", TypeKey = JsonEmitNode.Key, ParentId = "map", Config = WorkflowsTestSeed.EmptyJson(),
                        Inputs = WorkflowsTestSeed.Json("""{ "value": "{{item}}" }""") },
                new() { Id = "leaf", TypeKey = JsonEmitNode.Key, ParentId = "map", Config = WorkflowsTestSeed.EmptyJson(),
                        Inputs = WorkflowsTestSeed.Json("""{ "value": "{{item}}" }""") },
                new() { Id = "end", TypeKey = "builtin.terminal", Config = WorkflowsTestSeed.EmptyJson(),
                        Inputs = WorkflowsTestSeed.Json("""{ "count": "{{nodes.map.outputs.count}}" }""") },
            },
            Edges = new List<EdgeDefinition>
            {
                new() { From = "start", To = "map" },
                new() { From = "map", To = "end" },
                new() { From = "ms", To = "work" },
                new() { From = "work", To = "leaf" },
            },
        };
    }

    // manual → planner[emits json={subtasks:[{value:"plan-x"},{value:"plan-y"}]}] → map(items={{...json.subtasks}};
    // body: ms → echo[value={{item.value}}]) → terminal(reads results / results[0].value / results.length).
    private static WorkflowDefinition PlannerBridgeMapDefinition() => new()
    {
        SchemaVersion = 1,
        Nodes = new List<NodeDefinition>
        {
            new() { Id = "start", TypeKey = "trigger.manual", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
            new() { Id = "planner", TypeKey = JsonEmitNode.Key, Config = WorkflowsTestSeed.EmptyJson(),
                    Inputs = WorkflowsTestSeed.Json("""{ "json": { "subtasks": [ { "value": "plan-x" }, { "value": "plan-y" } ] } }""") },
            new() { Id = "map", TypeKey = "flow.map", Config = WorkflowsTestSeed.EmptyJson(),
                    Inputs = WorkflowsTestSeed.Json("""{ "items": "{{nodes.planner.outputs.json.subtasks}}" }""") },
            new() { Id = "ms", TypeKey = "flow.map_start", ParentId = "map", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
            new() { Id = "echo", TypeKey = JsonEmitNode.Key, ParentId = "map", Config = WorkflowsTestSeed.EmptyJson(),
                    Inputs = WorkflowsTestSeed.Json("""{ "value": "{{item.value}}" }""") },
            new() { Id = "end", TypeKey = "builtin.terminal", Config = WorkflowsTestSeed.EmptyJson(),
                    Inputs = WorkflowsTestSeed.Json("""{ "all": "{{nodes.map.outputs.results}}", "first": "{{nodes.map.outputs.results[0].value}}", "length": "{{nodes.map.outputs.results.length}}" }""") },
        },
        Edges = new List<EdgeDefinition>
        {
            new() { From = "start", To = "planner" },
            new() { From = "planner", To = "map" },
            new() { From = "map", To = "end" },
            new() { From = "ms", To = "echo" },
        },
    };

    // manual → map(body: ms → echo[item={{item}}, index={{index}}]) → terminal. echo's terminal output
    // carries both keys so the test can assert the per-element scope.
    private static WorkflowDefinition ItemAndIndexMapDefinition() => new()
    {
        SchemaVersion = 1,
        Nodes = new List<NodeDefinition>
        {
            new() { Id = "start", TypeKey = "trigger.manual", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
            new() { Id = "map", TypeKey = "flow.map", Config = WorkflowsTestSeed.EmptyJson(),
                    Inputs = WorkflowsTestSeed.Json("""{ "items": "{{trigger.things}}" }""") },
            new() { Id = "ms", TypeKey = "flow.map_start", ParentId = "map", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
            new() { Id = "echo", TypeKey = JsonEmitNode.Key, ParentId = "map", Config = WorkflowsTestSeed.EmptyJson(),
                    Inputs = WorkflowsTestSeed.Json("""{ "item": "{{item}}", "index": "{{index}}" }""") },
            new() { Id = "end", TypeKey = "builtin.terminal", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
        },
        Edges = new List<EdgeDefinition>
        {
            new() { From = "start", To = "map" },
            new() { From = "map", To = "end" },
            new() { From = "ms", To = "echo" },
        },
    };

    // manual → map(errorHandling; body: ms → boom[FlakyTestNode always fails]) → terminal.
    // Every branch hits boom; under continue each result is a failure marker, under terminate the map fails.
    private static WorkflowDefinition ContinueOnErrorMapDefinition(string flakyKey, string? errorHandling)
    {
        var mapConfig = errorHandling != null ? $$"""{ "errorHandling": "{{errorHandling}}" }""" : "{}";
        return new WorkflowDefinition
        {
            SchemaVersion = 1,
            Nodes = new List<NodeDefinition>
            {
                new() { Id = "start", TypeKey = "trigger.manual", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
                new() { Id = "map", TypeKey = "flow.map", Config = WorkflowsTestSeed.Json(mapConfig),
                        Inputs = WorkflowsTestSeed.Json("""{ "items": "{{trigger.things}}" }""") },
                new() { Id = "ms", TypeKey = "flow.map_start", ParentId = "map", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
                new() { Id = "boom", TypeKey = FlakyTestNode.Key, ParentId = "map",
                        Config = WorkflowsTestSeed.Json($$"""{ "key": "{{flakyKey}}", "failTimes": 99 }"""), Inputs = WorkflowsTestSeed.EmptyJson() },
                new() { Id = "end", TypeKey = "builtin.terminal", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
            },
            Edges = new List<EdgeDefinition>
            {
                new() { From = "start", To = "map" },
                new() { From = "map", To = "end" },
                new() { From = "ms", To = "boom" },
            },
        };
    }

    // manual → map(errorHandling; body: ms → gate[flow.wait_approval]) → terminal. The body node parks the run —
    // a first-class PR2 map body element. Used here only to prove the save-time validator now ACCEPTS it; the
    // durable parallel-branch suspend/resume behaviour itself is exercised in MapDurableResumeFlowTests.
    private static WorkflowDefinition SuspendingBodyMapDefinition(string? errorHandling)
    {
        var mapConfig = errorHandling != null ? $$"""{ "errorHandling": "{{errorHandling}}" }""" : "{}";
        return new WorkflowDefinition
        {
            SchemaVersion = 1,
            Nodes = new List<NodeDefinition>
            {
                new() { Id = "start", TypeKey = "trigger.manual", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
                new() { Id = "map", TypeKey = "flow.map", Config = WorkflowsTestSeed.Json(mapConfig),
                        Inputs = WorkflowsTestSeed.Json("""{ "items": "{{trigger.things}}" }""") },
                new() { Id = "ms", TypeKey = "flow.map_start", ParentId = "map", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
                new() { Id = "gate", TypeKey = "flow.wait_approval", ParentId = "map",
                        Config = WorkflowsTestSeed.Json("""{ "prompt": "go?" }"""), Inputs = WorkflowsTestSeed.EmptyJson() },
                new() { Id = "end", TypeKey = "builtin.terminal", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
            },
            Edges = new List<EdgeDefinition>
            {
                new() { From = "start", To = "map" },
                new() { From = "map", To = "end" },
                new() { From = "ms", To = "gate" },
            },
        };
    }

    // manual → map_0(body: ms_0 → map_1(body: ms_1 → … → map_{depth-1}(body: ms → echo))) → terminal.
    // Nesting `depth` maps so the engine's run-time depth guard (cap 8) fires when depth > 8.
    private static WorkflowDefinition DeeplyNestedMapDefinition(int depth)
    {
        var nodes = new List<NodeDefinition>
        {
            new() { Id = "start", TypeKey = "trigger.manual", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
            new() { Id = "end", TypeKey = "builtin.terminal", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
        };
        var edges = new List<EdgeDefinition>
        {
            new() { From = "start", To = "map_0" },
            new() { From = "map_0", To = "end" },
        };

        for (var i = 0; i < depth; i++)
        {
            var mapId = $"map_{i}";
            var parent = i == 0 ? null : $"map_{i - 1}";
            var itemsRef = i == 0 ? "{{trigger.things}}" : "{{item}}";   // outer binds the trigger array; inner re-fans its element

            nodes.Add(new() { Id = mapId, TypeKey = "flow.map", ParentId = parent,
                              Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.Json($$"""{ "items": "{{itemsRef}}" }""") });
            nodes.Add(new() { Id = $"ms_{i}", TypeKey = "flow.map_start", ParentId = mapId, Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() });

            if (i < depth - 1)
            {
                edges.Add(new() { From = $"ms_{i}", To = $"map_{i + 1}" });
                edges.Add(new() { From = $"map_{i + 1}", To = $"leaf_{i}" });
                nodes.Add(new() { Id = $"leaf_{i}", TypeKey = JsonEmitNode.Key, ParentId = mapId, Config = WorkflowsTestSeed.EmptyJson(),
                                  Inputs = WorkflowsTestSeed.Json("""{ "value": "{{item}}" }""") });
            }
            else
            {
                nodes.Add(new() { Id = $"echo_{i}", TypeKey = JsonEmitNode.Key, ParentId = mapId, Config = WorkflowsTestSeed.EmptyJson(),
                                  Inputs = WorkflowsTestSeed.Json("""{ "value": "{{item}}" }""") });
                edges.Add(new() { From = $"ms_{i}", To = $"echo_{i}" });
            }
        }

        return new WorkflowDefinition { SchemaVersion = 1, Nodes = nodes, Edges = edges };
    }
}
