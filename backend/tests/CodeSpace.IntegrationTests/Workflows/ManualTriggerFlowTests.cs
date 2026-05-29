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
/// End-to-end coverage for the <c>trigger.manual</c> Start node — the on-demand entry point
/// for workflows a person runs by hand (the Dify-style "fill a form, click Run" pattern).
///
/// <para>These prove the architectural claim that a workflow can be authored with NO event
/// source: a lone manual trigger satisfies the validator's "exactly one Trigger" rule, the
/// run starts through the same engine path as a webhook run, and the operator-supplied payload
/// flows by-name into the workflow's declared <c>{{input.*}}</c> contract. The Terminal echoes
/// its resolved inputs into <c>WorkflowRun.OutputsJson</c>, which is the observable signal.</para>
///
/// <para>Tier: high-fidelity — real CreateWorkflow command (runs DefinitionValidator), real
/// engine over real Postgres. No mocks.</para>
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public class ManualTriggerFlowTests
{
    private readonly PostgresFixture _fixture;
    public ManualTriggerFlowTests(PostgresFixture fixture) { _fixture = fixture; }

    [Fact]
    public async Task Manual_trigger_is_a_valid_sole_trigger_and_resolves_supplied_input()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var workflowId = await CreateWorkflowAsync(teamId, userId, ManualInputDefinition());
        var runId = await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, teamId, payloadJson: """{"ticket":"ABC-123"}""");

        await RunEngineAsync(runId);

        var run = await LoadRunAsync(runId);
        run.Status.ShouldBe(WorkflowRunStatus.Success,
            customMessage: "a workflow whose only trigger is trigger.manual must validate (one trigger + reachable terminal) and run to completion");

        run.OutputsJson.ShouldNotBeNull();
        using var outputs = JsonDocument.Parse(run.OutputsJson!);
        outputs.RootElement.GetProperty("from_input").GetString().ShouldBe("ABC-123",
            customMessage: "the manual payload must map by-name onto {{input.ticket}} — this is the Dify Start-form pattern");
        outputs.RootElement.GetProperty("from_trigger").GetString().ShouldBe("ABC-123",
            customMessage: "the manual node echoes the raw payload onto scope.Trigger so {{trigger.ticket}} also resolves");
    }

    [Fact]
    public async Task Manual_trigger_workflow_runs_with_an_empty_payload()
    {
        // The "just run it" case: a manual workflow that declares no inputs and is fired with
        // an empty body still walks start → terminal and lands Success.
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var workflowId = await CreateWorkflowAsync(teamId, userId, NoInputDefinition());
        var runId = await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, teamId, payloadJson: "{}");

        await RunEngineAsync(runId);

        var run = await LoadRunAsync(runId);
        run.Status.ShouldBe(WorkflowRunStatus.Success,
            customMessage: "a manual trigger with no declared inputs must still run on an empty payload");
        run.Error.ShouldBeNull();
    }

    // ─── Helpers ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Manual Start → Terminal. One declared input "ticket"; the Terminal echoes both the
    /// typed {{input.ticket}} and the raw {{trigger.ticket}} so we can assert the manual
    /// payload reaches both scope buckets.
    /// </summary>
    private static WorkflowDefinition ManualInputDefinition()
    {
        var inputDecl = new WorkflowVariable
        {
            Name = "ticket",
            Schema = WorkflowsTestSeed.Json("""{"type":"string"}"""),
            Required = false,
        };

        var terminalInputsJson = JsonSerializer.Serialize(new { from_input = "{{input.ticket}}", from_trigger = "{{trigger.ticket}}" });

        return new WorkflowDefinition
        {
            SchemaVersion = 1,
            Inputs = new[] { inputDecl },
            Nodes = new List<NodeDefinition>
            {
                new() { Id = "start", TypeKey = "trigger.manual", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
                new() { Id = "end",   TypeKey = "builtin.terminal", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.Json(terminalInputsJson) }
            },
            Edges = new List<EdgeDefinition>
            {
                new() { From = "start", To = "end" }
            }
        };
    }

    private static WorkflowDefinition NoInputDefinition() => new()
    {
        SchemaVersion = 1,
        Nodes = new List<NodeDefinition>
        {
            new() { Id = "start", TypeKey = "trigger.manual", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
            new() { Id = "end",   TypeKey = "builtin.terminal", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() }
        },
        Edges = new List<EdgeDefinition>
        {
            new() { From = "start", To = "end" }
        }
    };

    private async Task<Guid> CreateWorkflowAsync(Guid teamId, Guid userId, WorkflowDefinition definition)
    {
        using var scope = _fixture.BeginScopeAs(userId, teamId, Roles.Admin);
        var mediator = scope.Resolve<IMediator>();
        return await mediator.Send(new CreateWorkflowCommand
        {
            Name = "manual-" + Guid.NewGuid().ToString("N")[..8],
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

    private async Task<WorkflowRun> LoadRunAsync(Guid runId)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        return await db.WorkflowRun.AsNoTracking().SingleAsync(r => r.Id == runId);
    }
}
