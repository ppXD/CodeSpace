using System.Text.Json;
using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Workflows.Engine;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.IntegrationTests.Workflows.Infrastructure;
using CodeSpace.Messages.Authorization;
using CodeSpace.Messages.Commands.Workflows;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Dtos.Workflows;
using CodeSpace.Messages.Enums;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace CodeSpace.IntegrationTests.Workflows;

/// <summary>
/// End-to-end tests for the workflow-level variable system:
///   - <c>{{wf.X}}</c>     — author-defined workflow constants (from Definition.Variables)
///   - <c>{{input.X}}</c>  — per-run parameters (from Definition.Inputs)
///   - <c>{{trigger.X}}</c> — webhook / manual-run payload (already covered)
///
/// These tests exercise the path declaration → engine scope build → resolver walk all the
/// way to a node observing the resolved value in its outputs.
/// </summary>
[Collection(PostgresCollection.Name)]
public class WorkflowVariableScopeFlowTests
{
    private readonly PostgresFixture _fixture;
    public WorkflowVariableScopeFlowTests(PostgresFixture fixture) { _fixture = fixture; }

    [Fact]
    public async Task Workflow_variables_resolve_via_wf_scope()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);

        // Workflow-scoped variables live in the `variable` table, NOT in the definition
        // JSON. The workflow is created without any in-definition wf vars, then `wf.tag =
        // "hot"` is Set via IVariableService BEFORE the run is queued. logic.if uses
        // {{wf.tag}} == "hot" to pick the true branch.
        var def = new WorkflowDefinition
        {
            SchemaVersion = 1,
            Nodes = new List<NodeDefinition>
            {
                new() { Id = "start",  TypeKey = "trigger.pr.opened", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
                new() { Id = "gate",   TypeKey = "logic.if",          Config = WorkflowsTestSeed.Json("""{"condition":"{{wf.tag}} == \"hot\""}"""), Inputs = WorkflowsTestSeed.EmptyJson() },
                new() { Id = "hot",    TypeKey = "builtin.terminal",  Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
                new() { Id = "cold",   TypeKey = "builtin.terminal",  Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() }
            },
            Edges = new List<EdgeDefinition>
            {
                new() { From = "start", To = "gate" },
                new() { From = "gate",  To = "hot",  SourceHandle = "true" },
                new() { From = "gate",  To = "cold", SourceHandle = "false" }
            }
        };

        var workflowId = await CreateWorkflowAsync(teamId, userId, def);

        // Set wf.tag = "hot" via the unified Variable service BEFORE queuing the run.
        using (var setupScope = _fixture.BeginScope())
        {
            await setupScope.Resolve<CodeSpace.Core.Services.Variables.IVariableService>().SetAsync(
                CodeSpace.Messages.Enums.VariableScope.Workflow, workflowId, teamId,
                "tag", CodeSpace.Messages.Enums.VariableValueType.String,
                JsonDocument.Parse("\"hot\"").RootElement.Clone(),
                description: null, userId, CancellationToken.None);
        }

        var runId = await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, teamId);
        await RunEngineAsync(runId);

        var nodes = await LoadRunNodesAsync(runId);
        nodes["hot"].Status.ShouldBe(NodeStatus.Success);
        nodes["cold"].Status.ShouldBe(NodeStatus.Skipped);
    }

    [Fact]
    public async Task Manual_run_inputs_populate_input_scope_with_defaults_for_omitted()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);

        // Declares two inputs: severity (supplied) + reviewer (defaulted).
        // logic.if matches on {{input.severity}} == "high" → routes to high branch
        // logic.if matches on {{input.reviewer}} == "alice" via the default → also true
        var def = new WorkflowDefinition
        {
            SchemaVersion = 1,
            Inputs = new[]
            {
                new WorkflowVariable
                {
                    Name = "severity",
                    Schema = WorkflowsTestSeed.Json("""{"type":"string"}"""),
                    Required = true,
                },
                new WorkflowVariable
                {
                    Name = "reviewer",
                    Schema = WorkflowsTestSeed.Json("""{"type":"string"}"""),
                    Default = JsonDocument.Parse("\"alice\"").RootElement.Clone(),
                }
            },
            Nodes = new List<NodeDefinition>
            {
                new() { Id = "start", TypeKey = "trigger.pr.opened", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
                new() { Id = "g1",    TypeKey = "logic.if",          Config = WorkflowsTestSeed.Json("""{"condition":"{{input.severity}} == \"high\""}"""), Inputs = WorkflowsTestSeed.EmptyJson() },
                new() { Id = "g2",    TypeKey = "logic.if",          Config = WorkflowsTestSeed.Json("""{"condition":"{{input.reviewer}} == \"alice\""}"""), Inputs = WorkflowsTestSeed.EmptyJson() },
                new() { Id = "yes1",  TypeKey = "builtin.terminal",  Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
                new() { Id = "no1",   TypeKey = "builtin.terminal",  Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
                new() { Id = "yes2",  TypeKey = "builtin.terminal",  Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
                new() { Id = "no2",   TypeKey = "builtin.terminal",  Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
            },
            Edges = new List<EdgeDefinition>
            {
                new() { From = "start", To = "g1" },
                new() { From = "g1",    To = "yes1", SourceHandle = "true" },
                new() { From = "g1",    To = "no1",  SourceHandle = "false" },
                new() { From = "g1",    To = "g2",   SourceHandle = "true" },
                new() { From = "g2",    To = "yes2", SourceHandle = "true" },
                new() { From = "g2",    To = "no2",  SourceHandle = "false" },
            }
        };

        // Only `severity` supplied; `reviewer` falls back to "alice" default.
        var (_, runId) = await CreateAndSeedRunAsync(teamId, userId, def, triggerPayload: """{"severity":"high"}""");
        await RunEngineAsync(runId);

        var nodes = await LoadRunNodesAsync(runId);
        nodes["yes1"].Status.ShouldBe(NodeStatus.Success, "supplied input.severity must route true");
        nodes["yes2"].Status.ShouldBe(NodeStatus.Success, "default input.reviewer must route true");
        nodes["no1"].Status.ShouldBe(NodeStatus.Skipped);
        nodes["no2"].Status.ShouldBe(NodeStatus.Skipped);
    }

    [Fact]
    public async Task Input_scope_falls_back_to_trigger_payload_value_when_supplied()
    {
        // When the trigger payload supplies a key matching a declared input, the input
        // value takes the supplied value (NOT the default). Exercises the precedence rule.
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);

        var def = new WorkflowDefinition
        {
            SchemaVersion = 1,
            Inputs = new[]
            {
                new WorkflowVariable
                {
                    Name = "mode",
                    Schema = WorkflowsTestSeed.Json("""{"type":"string"}"""),
                    Default = JsonDocument.Parse("\"default-mode\"").RootElement.Clone(),
                }
            },
            Nodes = new List<NodeDefinition>
            {
                new() { Id = "start", TypeKey = "trigger.pr.opened", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
                new() { Id = "gate",  TypeKey = "logic.if",          Config = WorkflowsTestSeed.Json("""{"condition":"{{input.mode}} == \"custom\""}"""), Inputs = WorkflowsTestSeed.EmptyJson() },
                new() { Id = "yes",   TypeKey = "builtin.terminal",  Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
                new() { Id = "no",    TypeKey = "builtin.terminal",  Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() }
            },
            Edges = new List<EdgeDefinition>
            {
                new() { From = "start", To = "gate" },
                new() { From = "gate",  To = "yes", SourceHandle = "true" },
                new() { From = "gate",  To = "no",  SourceHandle = "false" }
            }
        };

        var (_, runId) = await CreateAndSeedRunAsync(teamId, userId, def, triggerPayload: """{"mode":"custom"}""");
        await RunEngineAsync(runId);

        var nodes = await LoadRunNodesAsync(runId);
        nodes["yes"].Status.ShouldBe(NodeStatus.Success, "supplied value must override default");
        nodes["no"].Status.ShouldBe(NodeStatus.Skipped);
    }

    // ─── Helpers ────────────────────────────────────────────────────────────────

    private async Task<(Guid WorkflowId, Guid RunId)> CreateAndSeedRunAsync(Guid teamId, Guid userId, WorkflowDefinition definition, string triggerPayload)
    {
        var workflowId = await CreateWorkflowAsync(teamId, userId, definition);
        var runId = await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, teamId, payloadJson: triggerPayload);
        return (workflowId, runId);
    }

    private async Task<Guid> CreateWorkflowAsync(Guid teamId, Guid userId, WorkflowDefinition definition)
    {
        using var scope = _fixture.BeginScopeAs(userId, teamId, Roles.Admin);
        var mediator = scope.Resolve<IMediator>();
        return await mediator.Send(new CreateWorkflowCommand
        {
            Name = "var-scope-" + Guid.NewGuid().ToString("N")[..8],
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

    private async Task<Dictionary<string, WorkflowRunNode>> LoadRunNodesAsync(Guid runId)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        return await db.WorkflowRunNode.AsNoTracking().Where(n => n.RunId == runId).ToDictionaryAsync(n => n.NodeId);
    }
}
