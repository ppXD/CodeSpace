using System.Text.Json;
using Autofac;
using CodeSpace.Core.Persistence.Db;
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
/// PR-B validator hardening, against the real save path (<c>CreateWorkflowCommand</c> → <c>DefinitionValidator</c>)
/// + the real engine. Pins that a previously-SILENT collision now fails at SAVE (the operator's real entry point),
/// and that the non-breaking guarantee holds end-to-end: a valid map with a non-reserved resultKey still saves AND
/// runs to Success with the array landing under that key.
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public class MapValidatorHardeningFlowTests
{
    private readonly PostgresFixture _fixture;

    public MapValidatorHardeningFlowTests(PostgresFixture fixture) { _fixture = fixture; }

    [Fact]
    public async Task A_map_resultKey_colliding_with_a_reserved_output_is_rejected_at_save()
    {
        // Previously SILENT: resultKey "count" overwrote the reducer's count output with the result array — a
        // wrong-data run that looked green. Now the save-time validator rejects it before any run.
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);

        var ex = await Should.ThrowAsync<WorkflowValidationException>(() =>
            CreateWorkflowAsync(teamId, userId, MapDefinition(resultKey: WorkflowOutputKeys.MapCount)));

        ex.Errors.ShouldContain(e => e.Contains("resultKey 'count'") && e.Contains("reserved"));
    }

    [Fact]
    public async Task A_map_with_no_items_binding_is_rejected_at_save()
    {
        // Previously SILENT: missing items fanned out zero branches → count:0 green no-op. Now rejected at save.
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);

        var ex = await Should.ThrowAsync<WorkflowValidationException>(() =>
            CreateWorkflowAsync(teamId, userId, MapDefinition(itemsJson: null)));

        ex.Errors.ShouldContain(e => e.Contains("no 'items' binding"));
    }

    [Fact]
    public async Task A_map_body_node_disconnected_from_start_is_rejected_at_save()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);

        var ex = await Should.ThrowAsync<WorkflowValidationException>(() =>
            CreateWorkflowAsync(teamId, userId, MapDefinition(withOrphanBodyNode: true)));

        ex.Errors.ShouldContain(e => e.Contains("not reachable from flow.map_start"));
    }

    [Fact]
    public async Task A_valid_map_with_a_custom_resultKey_still_saves_and_runs()
    {
        // The non-breaking guarantee end-to-end: a non-reserved, identifier resultKey saves AND runs, with the
        // reduced array landing under that key (plus the unchanged count/failed).
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var workflowId = await CreateWorkflowAsync(teamId, userId, MapDefinition(resultKey: "answers"));
        var runId = await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, teamId, payloadJson: """{ "things": ["a", "b", "c"] }""");

        using (var scope = _fixture.BeginScope())
            await scope.Resolve<IWorkflowEngine>().ExecuteRunAsync(runId, CancellationToken.None);

        using var verify = _fixture.BeginScope();
        var db = verify.Resolve<CodeSpaceDbContext>();
        (await db.WorkflowRun.AsNoTracking().SingleAsync(r => r.Id == runId)).Status.ShouldBe(WorkflowRunStatus.Success);

        var map = await db.WorkflowRunNode.AsNoTracking().SingleAsync(n => n.RunId == runId && n.NodeId == "map" && n.IterationKey == "");
        var outputs = JsonDocument.Parse(map.OutputsJson).RootElement;

        outputs.GetProperty("answers").GetArrayLength().ShouldBe(3);
        outputs.GetProperty("count").GetInt32().ShouldBe(3);
        outputs.GetProperty("failed").GetInt32().ShouldBe(0);
    }

    // ─── Helpers ────────────────────────────────────────────────────────────────

    private async Task<Guid> CreateWorkflowAsync(Guid teamId, Guid userId, WorkflowDefinition definition)
    {
        using var scope = _fixture.BeginScopeAs(userId, teamId, Roles.Admin);
        return await scope.Resolve<IMediator>().Send(new CreateWorkflowCommand
        {
            Name = "maphard-" + Guid.NewGuid().ToString("N")[..6],
            Description = null,
            Definition = definition,
            Activations = new List<WorkflowActivationInput>(),
            Enabled = true,
        });
    }

    // manual → map(items={{trigger.things}}; body: ms → echo[value={{item}}]) → terminal. Each knob tweaks one
    // off-baseline condition: a reserved/absent resultKey, an absent items binding, or a disconnected body node.
    private static WorkflowDefinition MapDefinition(string? resultKey = "results", string? itemsJson = """{ "items": "{{trigger.things}}" }""", bool withOrphanBodyNode = false)
    {
        var config = resultKey == null ? "{}" : $$"""{ "resultKey": "{{resultKey}}" }""";

        var nodes = new List<NodeDefinition>
        {
            new() { Id = "start", TypeKey = "trigger.manual", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
            new() { Id = "map", TypeKey = "flow.map", Config = WorkflowsTestSeed.Json(config), Inputs = itemsJson == null ? WorkflowsTestSeed.EmptyJson() : WorkflowsTestSeed.Json(itemsJson) },
            new() { Id = "ms", TypeKey = "flow.map_start", ParentId = "map", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
            new() { Id = "echo", TypeKey = JsonEmitNode.Key, ParentId = "map", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.Json("""{ "value": "{{item}}" }""") },
            new() { Id = "end", TypeKey = "builtin.terminal", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
        };

        if (withOrphanBodyNode)
            nodes.Add(new() { Id = "orphan", TypeKey = JsonEmitNode.Key, ParentId = "map", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.Json("""{ "value": "x" }""") });

        return new WorkflowDefinition
        {
            SchemaVersion = 1,
            Nodes = nodes,
            Edges = new List<EdgeDefinition>
            {
                new() { From = "start", To = "map" },
                new() { From = "map", To = "end" },
                new() { From = "ms", To = "echo" },
            },
        };
    }
}
