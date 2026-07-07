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
/// End-to-end tests for declared workflow outputs. A workflow declares
/// `Outputs: [{ name: "summary", schema: ... }]`. The Terminal node's Inputs map drives
/// what gets emitted: `Inputs: { summary: "{{trigger.title}}" }`. After Success, the run's
/// <c>OutputsJson</c> must contain the resolved values, ready for external consumers.
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public class WorkflowOutputsFlowTests
{
    private readonly PostgresFixture _fixture;
    public WorkflowOutputsFlowTests(PostgresFixture fixture) { _fixture = fixture; }

    [Fact]
    public async Task Terminal_inputs_become_workflow_outputs_on_workflow_run()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);

        // Workflow declares one output (summary). Terminal's Inputs map summary → trigger field.
        var def = new WorkflowDefinition
        {
            SchemaVersion = 1,
            Outputs = new[]
            {
                new WorkflowVariable
                {
                    Name = "summary",
                    Schema = WorkflowsTestSeed.Json("""{"type":"string"}"""),
                }
            },
            Nodes = new List<NodeDefinition>
            {
                new() { Id = "start", TypeKey = "trigger.pr.opened", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
                new() { Id = "end",   TypeKey = "builtin.terminal",  Config = WorkflowsTestSeed.EmptyJson(),
                        Inputs = WorkflowsTestSeed.Json("""{"summary":"{{trigger.title}}"}""") }
            },
            Edges = new List<EdgeDefinition>
            {
                new() { From = "start", To = "end" }
            }
        };

        var (_, runId) = await CreateAndSeedRunAsync(teamId, userId, def, triggerPayload: """{"title":"Fix the bug"}""");
        await RunEngineAsync(runId);

        using var verify = _fixture.BeginScope();
        var db = verify.Resolve<CodeSpaceDbContext>();
        var run = await db.WorkflowRun.AsNoTracking().SingleAsync(r => r.Id == runId);

        run.Status.ShouldBe(WorkflowRunStatus.Success);

        var outputs = JsonDocument.Parse(run.OutputsJson).RootElement;
        outputs.GetProperty("summary").GetString().ShouldBe("Fix the bug");
    }

    [Fact]
    public async Task Workflow_without_declared_outputs_yields_empty_object()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var (_, runId) = await CreateAndSeedRunAsync(teamId, userId, WorkflowsTestSeed.MinimalDefinition(), triggerPayload: "{}");
        await RunEngineAsync(runId);

        using var verify = _fixture.BeginScope();
        var db = verify.Resolve<CodeSpaceDbContext>();
        var run = await db.WorkflowRun.AsNoTracking().SingleAsync(r => r.Id == runId);

        run.Status.ShouldBe(WorkflowRunStatus.Success);

        // Terminal has empty Inputs → WorkflowOutputs stays empty → OutputsJson is "{}".
        var outputs = JsonDocument.Parse(run.OutputsJson).RootElement;
        outputs.ValueKind.ShouldBe(JsonValueKind.Object);
        outputs.EnumerateObject().ShouldBeEmpty();
    }

    [Fact]
    public async Task Outputs_compose_across_multiple_upstream_node_refs()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);

        // Terminal emits TWO outputs that pull from different upstream sources:
        //   matched     → from logic.if's matched output
        //   echo_title  → from trigger payload
        var def = new WorkflowDefinition
        {
            SchemaVersion = 1,
            Outputs = new[]
            {
                new WorkflowVariable { Name = "matched",    Schema = WorkflowsTestSeed.Json("""{"type":"boolean"}""") },
                new WorkflowVariable { Name = "echo_title", Schema = WorkflowsTestSeed.Json("""{"type":"string"}""") },
            },
            Nodes = new List<NodeDefinition>
            {
                new() { Id = "start",  TypeKey = "trigger.pr.opened", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
                new() { Id = "gate",   TypeKey = "logic.if",          Config = WorkflowsTestSeed.Json("""{"condition":"{{trigger.priority}} == \"high\""}"""), Inputs = WorkflowsTestSeed.EmptyJson() },
                new() { Id = "end",    TypeKey = "builtin.terminal",  Config = WorkflowsTestSeed.EmptyJson(),
                        Inputs = WorkflowsTestSeed.Json("""{"matched":"{{nodes.gate.outputs.matched}}","echo_title":"{{trigger.title}}"}""") }
            },
            Edges = new List<EdgeDefinition>
            {
                new() { From = "start", To = "gate" },
                new() { From = "gate",  To = "end", SourceHandle = "true" },
            }
        };

        var (_, runId) = await CreateAndSeedRunAsync(teamId, userId, def, triggerPayload: """{"priority":"high","title":"Compose test"}""");
        await RunEngineAsync(runId);

        using var verify = _fixture.BeginScope();
        var db = verify.Resolve<CodeSpaceDbContext>();
        var run = await db.WorkflowRun.AsNoTracking().SingleAsync(r => r.Id == runId);

        run.Status.ShouldBe(WorkflowRunStatus.Success);

        var outputs = JsonDocument.Parse(run.OutputsJson).RootElement;
        // Note: matched arrives as a string "True"/"true" because the engine resolves the
        // {{ref}} into the JSON-text representation. Accept either — the contract is "the
        // value is there".
        outputs.GetProperty("matched").ToString().ToLower().ShouldContain("true");
        outputs.GetProperty("echo_title").GetString().ShouldBe("Compose test");
    }

    // ─── Helpers (mirror sibling tests) ─────────────────────────────────────────

    [Fact]
    public async Task Sys_scope_resolves_into_terminal_outputs_end_to_end()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);

        // Terminal echoes three sys.* keys to outputs. After Success the run's OutputsJson
        // must contain real values — proving the engine populates Sys AND the resolver
        // walks `sys.*` paths AND the values round-trip through JSON serialisation.
        var def = new WorkflowDefinition
        {
            SchemaVersion = 1,
            Outputs = new[]
            {
                new WorkflowVariable { Name = "wf_id",     Schema = WorkflowsTestSeed.Json("""{"type":"string"}""") },
                new WorkflowVariable { Name = "run_id",    Schema = WorkflowsTestSeed.Json("""{"type":"string"}""") },
                new WorkflowVariable { Name = "trig_kind", Schema = WorkflowsTestSeed.Json("""{"type":"string"}""") },
            },
            Nodes = new List<NodeDefinition>
            {
                new() { Id = "start", TypeKey = "trigger.pr.opened", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
                new() { Id = "end",   TypeKey = "builtin.terminal",  Config = WorkflowsTestSeed.EmptyJson(),
                        Inputs = WorkflowsTestSeed.Json("""{"wf_id":"{{sys.workflow_id}}","run_id":"{{sys.workflow_run_id}}","src_type":"{{sys.source_type}}"}""") }
            },
            Edges = new List<EdgeDefinition>
            {
                new() { From = "start", To = "end" }
            }
        };

        var (workflowId, runId) = await CreateAndSeedRunAsync(teamId, userId, def, triggerPayload: "{}");
        await RunEngineAsync(runId);

        using var verify = _fixture.BeginScope();
        var db = verify.Resolve<CodeSpaceDbContext>();
        var run = await db.WorkflowRun.AsNoTracking().SingleAsync(r => r.Id == runId);

        run.Status.ShouldBe(WorkflowRunStatus.Success);

        var outputs = JsonDocument.Parse(run.OutputsJson).RootElement;
        outputs.GetProperty("wf_id").GetString().ShouldBe(workflowId.ToString(),
            "{{sys.workflow_id}} must resolve to the workflow's UUID");
        outputs.GetProperty("run_id").GetString().ShouldBe(runId.ToString(),
            "{{sys.workflow_run_id}} must resolve to this run's UUID — distinct from workflow_id");
        outputs.GetProperty("src_type").GetString().ShouldBe("manual",
            "{{sys.source_type}} should surface the run-request source_type string verbatim");
    }

    private async Task<(Guid WorkflowId, Guid RunId)> CreateAndSeedRunAsync(Guid teamId, Guid userId, WorkflowDefinition definition, string triggerPayload)
    {
        using var scope = _fixture.BeginScopeAs(userId, teamId, Roles.Admin);
        var mediator = scope.Resolve<IMediator>();
        var workflowId = await mediator.Send(new CreateWorkflowCommand
        {
            Name = "outputs-" + Guid.NewGuid().ToString("N")[..8],
            Description = null,
            Definition = definition,
            Activations = new List<WorkflowActivationInput>(),
            Enabled = true,
        });

        // Manual runs flow through a workflow_run_request row; the helper seeds that pair
        // so the engine's `Include(r => r.RunRequest)` finds the payload.
        var runId = await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, teamId, payloadJson: triggerPayload);
        return (workflowId, runId);
    }

    private async Task RunEngineAsync(Guid runId)
    {
        using var scope = _fixture.BeginScope();
        await scope.Resolve<IWorkflowEngine>().ExecuteRunAsync(runId, CancellationToken.None);
    }
}
