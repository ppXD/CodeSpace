using System.Text.Json;
using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Services.Workflows.Artifacts;
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
/// The durability audit's P2 fix: an oversize node output (a large HTTP body / LLM completion) must NOT land
/// inline in the append-only, never-deleted run-record ledger. The engine offloads such a value to the
/// content-addressed artifact store at the <c>node.completed</c> choke point and keeps only a compact ref — yet
/// downstream resolution of <c>{{nodes.X.outputs.*}}</c> still yields the FULL value, both in a single pass
/// (from in-process scope) and after a crash-resume re-walk (re-inflated from the store on rehydrate).
///
/// <para>Integration tier (real Postgres + real <c>ArtifactStore</c> + <c>LocalFileArtifactBlobBackend</c>): the
/// &gt;8 KiB value really round-trips through the out-of-band file backend. No model is exercised — a large
/// literal stands in for an HTTP body — so this is the rigorous deterministic tier, not a real-model surface.</para>
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public class NodeOutputArtifactOffloadFlowTests
{
    private readonly PostgresFixture _fixture;

    public NodeOutputArtifactOffloadFlowTests(PostgresFixture fixture) { _fixture = fixture; }

    // 32 KiB > the 8 KiB inline threshold ⇒ the value offloads to the out-of-band file backend.
    private static string LargeBody() => new('x', 32 * 1024);

    [Fact]
    public async Task Large_node_output_is_offloaded_from_the_ledger_yet_forwarded_in_full_in_a_single_pass()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var body = LargeBody();
        var workflowId = await CreateWorkflowAsync(teamId, userId, OffloadDefinition(withGate: false));
        var runId = await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, teamId, payloadJson: JsonSerializer.Serialize(new { body }));

        await RunEngineAsync(runId);

        using var verify = _fixture.BeginScope();
        var db = verify.Resolve<CodeSpaceDbContext>();

        var emit = await db.WorkflowRunNode.AsNoTracking().SingleAsync(n => n.RunId == runId && n.NodeId == "emit" && n.IterationKey == "");
        emit.OutputsJson.ShouldContain(NodeOutputArtifacts.RefKey, Case.Sensitive, "the oversize output is offloaded to an artifact ref, not inlined in the immutable ledger");
        emit.OutputsJson.Contains(body).ShouldBeFalse("the 32 KiB blob no longer sits inline in the ledger row");

        var artifact = await db.WorkflowArtifact.AsNoTracking().SingleAsync(a => a.TeamId == teamId);
        artifact.StorageUrl.ShouldNotBeNull("a >8 KiB value really lands in the out-of-band file backend, not inline in the DB row");
        artifact.InlineBytes.ShouldBeNull("the offloaded row keeps only the storage_url, not inline bytes");

        var run = await db.WorkflowRun.AsNoTracking().SingleAsync(r => r.Id == runId);
        run.Status.ShouldBe(WorkflowRunStatus.Success);
        run.OutputsJson.ShouldContain(body, Case.Sensitive, "single-pass resolution is unaffected — the terminal forwarded the FULL value from in-process scope");
    }

    [Fact]
    public async Task Offloaded_output_is_re_inflated_in_full_after_a_resume_rehydrate()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var body = LargeBody();
        var workflowId = await CreateWorkflowAsync(teamId, userId, OffloadDefinition(withGate: true));
        var runId = await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, teamId, payloadJson: JsonSerializer.Serialize(new { body }));

        await RunEngineAsync(runId);   // runs emit (offloaded), then parks at the approval gate (before the terminal)

        using (var parked = _fixture.BeginScope())
            (await parked.Resolve<CodeSpaceDbContext>().WorkflowRun.AsNoTracking().SingleAsync(r => r.Id == runId)).Status
                .ShouldBe(WorkflowRunStatus.Suspended, "precondition: the run parks before the downstream terminal runs");

        (await ApproveAsync(runId, teamId, userId)).ShouldBeTrue();
        await RunEngineAsync(runId);   // re-walk: RehydrateFromLedgerAsync must re-inflate emit's ref into scope

        using var verify = _fixture.BeginScope();
        var run = await verify.Resolve<CodeSpaceDbContext>().WorkflowRun.AsNoTracking().SingleAsync(r => r.Id == runId);
        run.Status.ShouldBe(WorkflowRunStatus.Success);
        run.OutputsJson.ShouldContain(body, Case.Sensitive, "rehydrate re-inflated the offloaded ref — the downstream terminal got the FULL value, never the bare ref");
    }

    [Fact]
    public async Task Large_map_container_aggregate_is_offloaded_from_the_ledger_yet_forwarded_in_full()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var body = LargeBody();
        var workflowId = await CreateWorkflowAsync(teamId, userId, LargeMapDefinition());
        var runId = await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, teamId, payloadJson: JsonSerializer.Serialize(new { things = new[] { body } }));

        await RunEngineAsync(runId);

        using var verify = _fixture.BeginScope();
        var db = verify.Resolve<CodeSpaceDbContext>();

        var run = await db.WorkflowRun.AsNoTracking().SingleAsync(r => r.Id == runId);
        run.Status.ShouldBe(WorkflowRunStatus.Success);

        // The flow.map CONTAINER aggregate (the results array) is offloaded at the container site — not inlined
        // in the append-only ledger (the gap the container-site fix closes).
        var mapNode = await db.WorkflowRunNode.AsNoTracking().SingleAsync(n => n.RunId == runId && n.NodeId == "map" && n.IterationKey == "");
        mapNode.OutputsJson.ShouldContain(NodeOutputArtifacts.RefKey, Case.Sensitive, "the large map aggregate is offloaded at the container site, not inlined");
        mapNode.OutputsJson.Contains(body).ShouldBeFalse("the aggregate blob is not inline in the ledger row");

        run.OutputsJson.ShouldContain(body, Case.Sensitive, "the terminal forwarded the FULL map results from in-process scope");
    }

    // manual → map(items={{trigger.things}}; body: ms → leaf[value={{item}}]) → terminal forwards the full results.
    private static WorkflowDefinition LargeMapDefinition() => new()
    {
        SchemaVersion = 1,
        Nodes = new List<NodeDefinition>
        {
            new() { Id = "start", TypeKey = "trigger.manual", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
            new() { Id = "map", TypeKey = "flow.map", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.Json("""{ "items": "{{trigger.things}}" }""") },
            new() { Id = "ms", TypeKey = "flow.map_start", ParentId = "map", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
            new() { Id = "leaf", TypeKey = JsonEmitNode.Key, ParentId = "map", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.Json("""{ "value": "{{item}}" }""") },
            new() { Id = "end", TypeKey = "builtin.terminal", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.Json("""{ "final": "{{nodes.map.outputs.results}}" }""") },
        },
        Edges = new List<EdgeDefinition>
        {
            new() { From = "start", To = "map" },
            new() { From = "map", To = "end" },
            new() { From = "ms", To = "leaf" },
        },
    };

    [Fact]
    public async Task Small_node_output_stays_inline_with_no_artifact()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var workflowId = await CreateWorkflowAsync(teamId, userId, OffloadDefinition(withGate: false));
        var runId = await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, teamId, payloadJson: JsonSerializer.Serialize(new { body = "small" }));

        await RunEngineAsync(runId);

        using var verify = _fixture.BeginScope();
        var db = verify.Resolve<CodeSpaceDbContext>();

        var emit = await db.WorkflowRunNode.AsNoTracking().SingleAsync(n => n.RunId == runId && n.NodeId == "emit" && n.IterationKey == "");
        emit.OutputsJson.ShouldContain("small");
        emit.OutputsJson.Contains(NodeOutputArtifacts.RefKey).ShouldBeFalse("a within-budget value stays inline — no offload");

        (await db.WorkflowArtifact.AsNoTracking().AnyAsync(a => a.TeamId == teamId)).ShouldBeFalse("no artifact written for a small output");
    }

    // start → emit (echoes {{trigger.body}} as output `body`) → [gate?] → terminal (forwards {{nodes.emit.outputs.body}}).
    private static WorkflowDefinition OffloadDefinition(bool withGate)
    {
        var nodes = new List<NodeDefinition>
        {
            new() { Id = "start", TypeKey = "trigger.manual", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
            new() { Id = "emit", TypeKey = JsonEmitNode.Key, Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.Json("""{ "body": "{{trigger.body}}" }""") },
            new() { Id = "end", TypeKey = "builtin.terminal", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.Json("""{ "body": "{{nodes.emit.outputs.body}}" }""") },
        };

        var edges = new List<EdgeDefinition> { new() { From = "start", To = "emit" } };

        if (withGate)
        {
            nodes.Insert(2, new() { Id = "gate", TypeKey = "flow.wait_approval", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() });
            edges.Add(new() { From = "emit", To = "gate" });
            edges.Add(new() { From = "gate", To = "end" });
        }
        else
            edges.Add(new() { From = "emit", To = "end" });

        return new WorkflowDefinition { SchemaVersion = 1, Nodes = nodes, Edges = edges };
    }

    private async Task<Guid> CreateWorkflowAsync(Guid teamId, Guid userId, WorkflowDefinition definition)
    {
        using var scope = _fixture.BeginScopeAs(userId, teamId, Roles.Admin);
        return await scope.Resolve<IMediator>().Send(new CreateWorkflowCommand
        {
            Name = "offload-" + Guid.NewGuid().ToString("N")[..6],
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

    private async Task<bool> ApproveAsync(Guid runId, Guid teamId, Guid userId)
    {
        using var scope = _fixture.BeginScopeAs(userId, teamId, Roles.Admin);
        return await scope.Resolve<IMediator>().Send(new ResumeRunCommand { RunId = runId, Approved = true, Comment = "ok" });
    }
}
