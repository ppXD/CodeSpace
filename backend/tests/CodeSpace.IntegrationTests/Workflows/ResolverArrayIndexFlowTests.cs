using System.Text.Json;
using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Services.Variables;
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
/// End-to-end proof that array indexing + <c>.length</c> resolve through the engine's real
/// node-input resolution hot path (<c>ExecuteNodeAsync → VariableResolver.ResolveBag</c>) into a
/// run's outputs — the dependency that lets a future <c>flow.map</c> bind <c>{{…subtasks[0]}}</c>
/// and size a fan-out off <c>{{…subtasks.length}}</c>.
///
/// Also pins the security fix: the Terminal secret-leak guard must still fire when a secret is
/// referenced through an index / <c>.length</c> accessor — the extracted path is normalized back
/// to the secret's base before the exact-match check, so the accessor can't slip past the guard.
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public class ResolverArrayIndexFlowTests
{
    private readonly PostgresFixture _fixture;

    public ResolverArrayIndexFlowTests(PostgresFixture fixture) { _fixture = fixture; }

    [Fact]
    public async Task Index_and_length_resolve_through_the_engine_into_outputs()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);

        // Terminal maps two outputs off a trigger ARRAY: first element (index) + element count (.length).
        var def = new WorkflowDefinition
        {
            SchemaVersion = 1,
            Outputs = new[]
            {
                new WorkflowVariable { Name = "first", Schema = WorkflowsTestSeed.Json("""{"type":"string"}""") },
                new WorkflowVariable { Name = "count", Schema = WorkflowsTestSeed.Json("""{"type":"integer"}""") },
            },
            Nodes = new List<NodeDefinition>
            {
                new() { Id = "start", TypeKey = "trigger.pr.opened", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
                new() { Id = "end",   TypeKey = "builtin.terminal",  Config = WorkflowsTestSeed.EmptyJson(),
                        Inputs = WorkflowsTestSeed.Json("""{"first":"{{trigger.items[0]}}","count":"{{trigger.items.length}}"}""") },
            },
            Edges = new List<EdgeDefinition> { new() { From = "start", To = "end" } }
        };

        var runId = await CreateAndSeedRunAsync(teamId, userId, def, triggerPayload: """{"items":["alpha","beta","gamma"]}""");
        await RunEngineAsync(runId);

        using var verify = _fixture.BeginScope();
        var run = await verify.Resolve<CodeSpaceDbContext>().WorkflowRun.AsNoTracking().SingleAsync(r => r.Id == runId);

        run.Status.ShouldBe(WorkflowRunStatus.Success);

        var outputs = JsonDocument.Parse(run.OutputsJson).RootElement;
        outputs.GetProperty("first").GetString().ShouldBe("alpha", "an indexed trigger ref resolves through the engine hot path");
        outputs.GetProperty("count").ToString().ShouldBe("3", ".length resolved to the element count (3) end-to-end");
    }

    [Fact]
    public async Task Terminal_referencing_an_indexed_secret_is_still_blocked_by_the_guard()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);

        using (var setup = _fixture.BeginScope())
        {
            await setup.Resolve<IVariableService>().SetAsync(
                VariableScope.Team, teamId, teamId, "API_KEY", VariableValueType.Secret,
                JsonString("must-not-leak"), description: null, userId, CancellationToken.None);
        }

        // The accessor [0] must NOT let the secret slip past the leak guard: ExtractReferencedPaths
        // normalizes "team.API_KEY[0]" back to "team.API_KEY" before the exact-match secret check.
        var def = new WorkflowDefinition
        {
            SchemaVersion = 1,
            Outputs = new[] { new WorkflowVariable { Name = "key", Schema = WorkflowsTestSeed.Json("""{"type":"string"}""") } },
            Nodes = new List<NodeDefinition>
            {
                new() { Id = "start", TypeKey = "trigger.pr.opened", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
                new() { Id = "end",   TypeKey = "builtin.terminal",  Config = WorkflowsTestSeed.EmptyJson(),
                        Inputs = WorkflowsTestSeed.Json("""{"key":"{{team.API_KEY[0]}}"}""") },
            },
            Edges = new List<EdgeDefinition> { new() { From = "start", To = "end" } }
        };

        var runId = await CreateAndSeedRunAsync(teamId, userId, def, triggerPayload: "{}");
        await RunEngineAsync(runId);

        using var verify = _fixture.BeginScope();
        var run = await verify.Resolve<CodeSpaceDbContext>().WorkflowRun.AsNoTracking().SingleAsync(r => r.Id == runId);

        run.Status.ShouldBe(WorkflowRunStatus.Failure, "an indexed secret ref must still trip the leak guard");
        run.Error!.Contains("team.API_KEY").ShouldBeTrue("the guard names the secret base, not the indexed accessor; got: " + run.Error);
        run.OutputsJson.Contains("must-not-leak").ShouldBeFalse("the secret must never reach OutputsJson; got: " + run.OutputsJson);
    }

    // ─── Helpers (mirror WorkflowOutputsFlowTests / WorkflowEngineSecretsFlowTests) ──

    private static JsonElement JsonString(string s) => JsonDocument.Parse(JsonSerializer.Serialize(s)).RootElement.Clone();

    private async Task<Guid> CreateAndSeedRunAsync(Guid teamId, Guid userId, WorkflowDefinition definition, string triggerPayload)
    {
        using var scope = _fixture.BeginScopeAs(userId, teamId, Roles.Admin);
        var workflowId = await scope.Resolve<IMediator>().Send(new CreateWorkflowCommand
        {
            Name = "arr-index-" + Guid.NewGuid().ToString("N")[..8],
            Description = null,
            Definition = definition,
            Activations = new List<WorkflowActivationInput>(),
            Enabled = true,
        });

        return await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, teamId, payloadJson: triggerPayload);
    }

    private async Task RunEngineAsync(Guid runId)
    {
        using var scope = _fixture.BeginScope();
        await scope.Resolve<IWorkflowEngine>().ExecuteRunAsync(runId, CancellationToken.None);
    }
}
