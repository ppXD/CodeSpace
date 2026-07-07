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
/// End-to-end engine runs against a real Postgres. Each test creates a workflow + manually
/// seeds a workflow_run row, then invokes <see cref="IWorkflowEngine.ExecuteRunAsync"/>
/// directly (skipping the trigger dispatcher to keep the engine the unit under test).
/// Asserts on the persisted workflow_run + workflow_run_node rows — the public contract
/// the run-detail UI and downstream consumers depend on.
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public class WorkflowEngineFlowTests
{
    private readonly PostgresFixture _fixture;

    public WorkflowEngineFlowTests(PostgresFixture fixture) { _fixture = fixture; }

    [Fact]
    public async Task Linear_definition_completes_with_success_and_writes_one_row_per_node()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var workflowId = await CreateWithDefinitionAsync(teamId, userId, WorkflowsTestSeed.MinimalDefinition());
        var runId = await SeedRunAsync(workflowId);

        await RunEngineAsync(runId);

        using var verify = _fixture.BeginScope();
        var db = verify.Resolve<CodeSpaceDbContext>();

        var run = await db.WorkflowRun.AsNoTracking().SingleAsync(r => r.Id == runId);
        run.Status.ShouldBe(WorkflowRunStatus.Success);
        run.Error.ShouldBeNull();
        run.StartedAt.ShouldNotBeNull();
        run.CompletedAt.ShouldNotBeNull();
        run.CompletedAt.Value.ShouldBeGreaterThanOrEqualTo(run.StartedAt.Value);

        var nodes = await db.WorkflowRunNode.AsNoTracking().Where(n => n.RunId == runId).OrderBy(n => n.StartedAt).ToListAsync();
        nodes.Select(n => n.NodeId).ShouldBe(new[] { "start", "end" }, ignoreOrder: false);
        nodes.ShouldAllBe(n => n.Status == NodeStatus.Success);
        nodes.ShouldAllBe(n => n.CompletedAt != null);
    }

    [Fact]
    public async Task Branch_routes_only_to_matching_handle_other_branch_is_skipped()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);

        // trigger → if (matched=true via {{trigger.x}}==1) → trueEnd (Success)
        //                                                  → falseEnd (Skipped)
        var def = new WorkflowDefinition
        {
            SchemaVersion = 1,
            Nodes = new List<NodeDefinition>
            {
                new() { Id = "start",    TypeKey = "trigger.pr.opened", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
                new() { Id = "branch",   TypeKey = "logic.if",          Config = WorkflowsTestSeed.Json("""{"condition":"{{trigger.x}} == 1"}"""), Inputs = WorkflowsTestSeed.EmptyJson() },
                new() { Id = "trueEnd",  TypeKey = "builtin.terminal",  Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
                new() { Id = "falseEnd", TypeKey = "builtin.terminal",  Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() }
            },
            Edges = new List<EdgeDefinition>
            {
                new() { From = "start",  To = "branch" },
                new() { From = "branch", To = "trueEnd",  SourceHandle = "true" },
                new() { From = "branch", To = "falseEnd", SourceHandle = "false" }
            }
        };

        var workflowId = await CreateWithDefinitionAsync(teamId, userId, def);
        var runId = await SeedRunAsync(workflowId, """{"x": 1}""");

        await RunEngineAsync(runId);

        using var verify = _fixture.BeginScope();
        var db = verify.Resolve<CodeSpaceDbContext>();

        var nodesByid = await db.WorkflowRunNode.AsNoTracking().Where(n => n.RunId == runId).ToDictionaryAsync(n => n.NodeId);
        nodesByid["start"].Status.ShouldBe(NodeStatus.Success);
        nodesByid["branch"].Status.ShouldBe(NodeStatus.Success);
        nodesByid["trueEnd"].Status.ShouldBe(NodeStatus.Success, "the true handle should have fired");
        nodesByid["falseEnd"].Status.ShouldBe(NodeStatus.Skipped, "the false handle's downstream must skip");
    }

    [Fact]
    public async Task Iterate_emits_aggregated_results_array()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);

        var def = new WorkflowDefinition
        {
            SchemaVersion = 1,
            Nodes = new List<NodeDefinition>
            {
                new() { Id = "start", TypeKey = "trigger.pr.opened", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
                new() { Id = "loop",  TypeKey = "flow.iterate",
                        Config = WorkflowsTestSeed.Json("""{"template":"item-{{item}}-at-{{index}}"}"""),
                        Inputs = WorkflowsTestSeed.Json("""{"items":["a","b","c"]}""") },
                new() { Id = "end",   TypeKey = "builtin.terminal",  Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() }
            },
            Edges = new List<EdgeDefinition>
            {
                new() { From = "start", To = "loop" },
                new() { From = "loop",  To = "end" }
            }
        };

        var workflowId = await CreateWithDefinitionAsync(teamId, userId, def);
        var runId = await SeedRunAsync(workflowId);
        await RunEngineAsync(runId);

        using var verify = _fixture.BeginScope();
        var db = verify.Resolve<CodeSpaceDbContext>();

        var loopNode = await db.WorkflowRunNode.AsNoTracking().SingleAsync(n => n.RunId == runId && n.NodeId == "loop");
        loopNode.Status.ShouldBe(NodeStatus.Success);

        var outputs = JsonDocument.Parse(loopNode.OutputsJson).RootElement;
        outputs.GetProperty("count").GetInt32().ShouldBe(3);
        var results = outputs.GetProperty("results").EnumerateArray().Select(e => e.GetString()).ToList();
        results.ShouldBe(new[] { "item-a-at-0", "item-b-at-1", "item-c-at-2" });
    }

    [Fact]
    public async Task Run_already_terminal_is_a_noop_idempotent_replay()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var workflowId = await CreateWithDefinitionAsync(teamId, userId, WorkflowsTestSeed.MinimalDefinition());
        var runId = await SeedRunAsync(workflowId);

        await RunEngineAsync(runId);
        // Re-invoke — should NOT change anything (no extra workflow_run_node rows, status stays Success).
        await RunEngineAsync(runId);

        using var verify = _fixture.BeginScope();
        var db = verify.Resolve<CodeSpaceDbContext>();
        var run = await db.WorkflowRun.AsNoTracking().SingleAsync(r => r.Id == runId);
        run.Status.ShouldBe(WorkflowRunStatus.Success);

        var nodeCount = await db.WorkflowRunNode.AsNoTracking().CountAsync(n => n.RunId == runId);
        nodeCount.ShouldBe(2, "re-running a Success run must not duplicate workflow_run_node rows");
    }

    [Fact]
    public async Task Trigger_outputs_round_trip_through_scope_to_downstream_node()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);

        // logic.if matches on {{trigger.priority}} — drives routing → asserts trigger payload
        // flows into scope and is visible to downstream nodes through {{trigger.*}}.
        var def = new WorkflowDefinition
        {
            SchemaVersion = 1,
            Nodes = new List<NodeDefinition>
            {
                new() { Id = "start",  TypeKey = "trigger.pr.opened", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
                new() { Id = "gate",   TypeKey = "logic.if",          Config = WorkflowsTestSeed.Json("""{"condition":"{{trigger.priority}} == \"high\""}"""), Inputs = WorkflowsTestSeed.EmptyJson() },
                new() { Id = "highEnd",TypeKey = "builtin.terminal",  Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
                new() { Id = "lowEnd", TypeKey = "builtin.terminal",  Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() }
            },
            Edges = new List<EdgeDefinition>
            {
                new() { From = "start", To = "gate" },
                new() { From = "gate",  To = "highEnd", SourceHandle = "true" },
                new() { From = "gate",  To = "lowEnd",  SourceHandle = "false" }
            }
        };

        var workflowId = await CreateWithDefinitionAsync(teamId, userId, def);
        var runId = await SeedRunAsync(workflowId, """{"priority":"high"}""");
        await RunEngineAsync(runId);

        using var verify = _fixture.BeginScope();
        var db = verify.Resolve<CodeSpaceDbContext>();
        var byId = await db.WorkflowRunNode.AsNoTracking().Where(n => n.RunId == runId).ToDictionaryAsync(n => n.NodeId);

        byId["highEnd"].Status.ShouldBe(NodeStatus.Success);
        byId["lowEnd"].Status.ShouldBe(NodeStatus.Skipped);
    }

    // ─── Helpers ────────────────────────────────────────────────────────────────

    private async Task<Guid> CreateWithDefinitionAsync(Guid teamId, Guid userId, WorkflowDefinition definition)
    {
        using var scope = _fixture.BeginScopeAs(userId, teamId, Roles.Admin);
        var mediator = scope.Resolve<IMediator>();
        return await mediator.Send(new CreateWorkflowCommand
        {
            Name = "engine-" + Guid.NewGuid().ToString("N")[..8],
            Description = null,
            Definition = definition,
            Activations = new List<WorkflowActivationInput>(),
            Enabled = true,
        });
    }

    private async Task<Guid> SeedRunAsync(Guid workflowId, string triggerPayloadJson = "{}")
    {
        // Resolve the workflow's team so the helper can stamp request.team_id correctly.
        using var lookupScope = _fixture.BeginScope();
        var teamId = await lookupScope.Resolve<CodeSpaceDbContext>().Workflow.AsNoTracking()
            .Where(w => w.Id == workflowId)
            .Select(w => w.TeamId)
            .SingleAsync();

        return await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, teamId, payloadJson: triggerPayloadJson);
    }

    private async Task RunEngineAsync(Guid runId)
    {
        using var scope = _fixture.BeginScope();
        var engine = scope.Resolve<IWorkflowEngine>();
        await engine.ExecuteRunAsync(runId, CancellationToken.None);
    }
}
