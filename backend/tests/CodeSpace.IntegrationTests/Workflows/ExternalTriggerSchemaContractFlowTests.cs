using System.Text.Json;
using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Services.Workflows;
using CodeSpace.Core.Services.Workflows.Engine;
using CodeSpace.Core.Services.Workflows.ExternalTriggers;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.IntegrationTests.Workflows.Infrastructure;
using CodeSpace.Messages.Commands.Workflows;
using CodeSpace.Messages.Dtos.Workflows;
using CodeSpace.Messages.Enums;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace CodeSpace.IntegrationTests.Workflows;

[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public sealed class ExternalTriggerSchemaContractFlowTests(PostgresFixture fixture)
{
    [Fact]
    public async Task Boolean_and_untyped_property_schemas_are_saved_and_resolve_through_workflow_inputs()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(fixture, false);
        using var scope = fixture.BeginScopeAs(userId, teamId);
        var schema = WorkflowsTestSeed.Json("""{"type":"object","properties":{"allowed":true,"blocked":false,"untyped":{}}}""");
        var config = JsonSerializer.SerializeToElement(new { inputSchema = schema });
        var definition = WorkflowsTestSeed.MinimalDefinition() with
        {
            Outputs = [new WorkflowVariable { Name = "allowed", Schema = WorkflowsTestSeed.Json("true") },
                new WorkflowVariable { Name = "untyped", Schema = WorkflowsTestSeed.Json("{}") }],
            Nodes = [new NodeDefinition { Id = "start", TypeKey = "trigger.external", Config = config, Inputs = WorkflowsTestSeed.EmptyJson() },
                new NodeDefinition { Id = "end", TypeKey = "builtin.terminal", Config = WorkflowsTestSeed.EmptyJson(),
                    Inputs = WorkflowsTestSeed.Json("""{"allowed":"{{input.allowed}}","untyped":"{{input.untyped}}"} """) }],
        };

        var workflowId = await scope.Resolve<IMediator>().Send(new CreateWorkflowCommand
        {
            Name = "Boolean schema contract", Definition = definition,
            Activations = [new WorkflowActivationInput { TypeKey = "trigger.external", Config = config }],
        });
        var version = await scope.Resolve<CodeSpaceDbContext>().WorkflowVersion.AsNoTracking()
            .SingleAsync(candidate => candidate.WorkflowId == workflowId && candidate.Version == 1);
        var saved = JsonSerializer.Deserialize<WorkflowDefinition>(version.DefinitionJson, WorkflowJson.Options)!;

        saved.Inputs.Select(input => input.Name).ShouldBe(["allowed", "blocked", "untyped"]);
        saved.Inputs[0].Schema.ValueKind.ShouldBe(JsonValueKind.True);
        saved.Inputs[1].Schema.ValueKind.ShouldBe(JsonValueKind.False);
        saved.Inputs[2].Schema.ValueKind.ShouldBe(JsonValueKind.Object);

        var trigger = await scope.Resolve<CodeSpaceDbContext>().WorkflowExternalTrigger.AsNoTracking()
            .SingleAsync(candidate => candidate.WorkflowId == workflowId);
        var key = scope.Resolve<ExternalTriggerKeySealer>().Open(trigger.KeyCiphertext);
        ExternalTriggerOutcome outcome;
        using (var receive = fixture.BeginScope())
            outcome = await receive.Resolve<IExternalTriggerIngress>().ReceiveAsync(
                new(trigger.Id, key, null, """{"allowed":true,"untyped":{"value":"kept"}}""", false), CancellationToken.None);
        outcome.Kind.ShouldBe(ExternalTriggerOutcomeKind.Accepted);
        using (var engine = fixture.BeginScope())
            await engine.Resolve<IWorkflowEngine>().ExecuteRunAsync(outcome.RunId, CancellationToken.None);
        using var verify = fixture.BeginScope();
        var run = await verify.Resolve<CodeSpaceDbContext>().WorkflowRun.AsNoTracking().SingleAsync(candidate => candidate.Id == outcome.RunId);
        run.Status.ShouldBe(WorkflowRunStatus.Success);
        using var outputs = JsonDocument.Parse(run.OutputsJson);
        outputs.RootElement.GetProperty("allowed").GetBoolean().ShouldBeTrue();
        outputs.RootElement.GetProperty("untyped").GetProperty("value").GetString().ShouldBe("kept");
    }
}
