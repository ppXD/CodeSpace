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

    [Fact]
    public async Task The_real_editor_schema_can_be_saved_and_enforced_at_ingress()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !Directory.Exists(Path.Combine(root.FullName, "frontend"))) root = root.Parent;
        root.ShouldNotBeNull();
        var path = Path.Combine(root.FullName, "tmp", "external-trigger-schema-fixture.json");
        File.Exists(path).ShouldBeTrue("run frontend external-trigger-schema-contract.test.ts first");
        using var schema = JsonDocument.Parse(await File.ReadAllTextAsync(path));
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(fixture, false);
        using var scope = fixture.BeginScopeAs(userId, teamId);
        var config = JsonSerializer.SerializeToElement(new { inputSchema = schema.RootElement });
        var definition = WorkflowsTestSeed.MinimalDefinition() with
        {
            Nodes = [new NodeDefinition { Id = "start", TypeKey = "trigger.external", Config = config, Inputs = WorkflowsTestSeed.EmptyJson() },
                new NodeDefinition { Id = "end", TypeKey = "builtin.terminal", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() }],
        };
        var workflowId = await scope.Resolve<IMediator>().Send(new CreateWorkflowCommand
        {
            Name = "Editor contract", Definition = definition,
            Activations = [new WorkflowActivationInput { TypeKey = "trigger.external", Config = config }],
        });
        var row = await scope.Resolve<CodeSpaceDbContext>().WorkflowExternalTrigger.AsNoTracking().SingleAsync(t => t.WorkflowId == workflowId);
        var key = scope.Resolve<ExternalTriggerKeySealer>().Open(row.KeyCiphertext);
        const string valid = """{"text":"short","paragraph":"paragraph","select":"a","number":1.5,"boolean":true,"repository":"repo","conversation":"chat"}""";
        (await Deliver(valid)).Kind.ShouldBe(ExternalTriggerOutcomeKind.Accepted);
        (await Deliver(valid.Replace("short", "toolong"))).Kind.ShouldBe(ExternalTriggerOutcomeKind.InvalidPayload);
        (await Deliver(valid.Replace("\"select\":\"a\"", "\"select\":\"unknown\""))).Kind.ShouldBe(ExternalTriggerOutcomeKind.InvalidPayload);
        (await Deliver("{}")).Kind.ShouldBe(ExternalTriggerOutcomeKind.InvalidPayload);

        async Task<ExternalTriggerOutcome> Deliver(string body)
        {
            using var receive = fixture.BeginScope();
            return await receive.Resolve<IExternalTriggerIngress>().ReceiveAsync(new(row.Id, key, null, body, false), CancellationToken.None);
        }
    }
}
