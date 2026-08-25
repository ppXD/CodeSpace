using System.Text.Json;
using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Services.Variables;
using CodeSpace.Core.Services.Workflows.Engine;
using CodeSpace.Core.Services.Workflows.Runtime;
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
/// The redaction plane's WRONG-VALUE hole: a node whose outputs carried a resolved workflow secret is written to
/// the public ledger REDACTED, and the originals live only in the encrypted same-record sidecar. When that sidecar
/// never committed, a later rehydrate found no sidecar and silently fell back to the redacted ledger row — handing
/// downstream execution the literal marker <c>[REDACTED]</c> AS IF IT WERE THE SECRET. Not a failure: a wrong value,
/// downstream of the only log line that mentions it.
///
/// <para>Fidelity: HIGH (real Postgres, the real engine, the real record logger, the real sidecar store). The one
/// simulated element is the sidecar store's WRITE, substituted for a throwing double so the engine's own
/// transaction-failure arm — the production path that settles a redacted <c>node.completed</c> with no sidecar —
/// really runs. Nothing about the reader, the ledger row, or the resume path is faked.</para>
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public class RedactedOutputRecoveryFlowTests
{
    private const string Sentinel = "sk-RECOVERY-GAP-DO-NOT-LEAK-12345678";

    private readonly PostgresFixture _fixture;

    public RedactedOutputRecoveryFlowTests(PostgresFixture fixture) { _fixture = fixture; }

    [Fact]
    public async Task A_resumed_node_whose_recovery_sidecar_never_committed_refuses_to_feed_downstream_the_redaction_marker()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        await SeedSecretAsync(teamId, userId, Sentinel);

        var workflowId = await CreateWorkflowAsync(teamId, userId, EchoThenGateDefinition());
        var runId = await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, teamId);

        // The engine settles `emit` while its sidecar write throws — its transaction-failure arm writes the
        // redacted node.completed through an isolated scope and never retries the sidecar.
        await RunEngineAsync(runId, sidecarSaveFails: true);

        using (var parked = _fixture.BeginScope())
        {
            var db = parked.Resolve<CodeSpaceDbContext>();
            (await db.WorkflowRun.AsNoTracking().SingleAsync(r => r.Id == runId)).Status.ShouldBe(WorkflowRunStatus.Suspended, "precondition: the run parks at the gate after emit settled");

            var emit = await db.WorkflowRunNode.AsNoTracking().SingleAsync(n => n.RunId == runId && n.NodeId == "emit" && n.IterationKey == "");
            emit.Status.ShouldBe(NodeStatus.Success, "precondition: the node is RECORDED SETTLED — it will never be re-dispatched");
            emit.OutputsJson.ShouldContain(PersistenceSecretRedactor.Marker, Case.Sensitive, "precondition: the public row holds the marker, not the secret");
            (await db.WorkflowRunSensitiveRecordPayload.AsNoTracking().AnyAsync(p => p.RecordId == emit.RecordId))
                .ShouldBeFalse("precondition: the encrypted recovery sidecar for that exact record never committed");
        }

        (await ApproveAsync(runId, teamId, userId)).ShouldBeTrue();
        await RunEngineAsync(runId, sidecarSaveFails: false);

        using var verify = _fixture.BeginScope();
        var verifyDb = verify.Resolve<CodeSpaceDbContext>();

        var downstream = await verifyDb.WorkflowRunNode.AsNoTracking()
            .Where(n => n.RunId == runId && n.NodeId == "after").Select(n => n.OutputsJson).SingleOrDefaultAsync();

        downstream.ShouldBeNull($"the downstream node must NEVER run on the redaction marker standing in for the secret — it consumed {downstream}");

        var run = await verifyDb.WorkflowRun.AsNoTracking().SingleAsync(r => r.Id == runId);
        run.Status.ShouldBe(WorkflowRunStatus.Failure, "a resume that cannot recover a redacted node's original must fail loudly, not proceed on a placeholder");
        run.Error.ShouldNotBeNull();
        run.Error.ShouldContain("emit", Case.Sensitive, "the failure must name the node whose original is gone so an operator can rerun from it");
        run.OutputsJson.ShouldNotContain(PersistenceSecretRedactor.Marker, Case.Sensitive, "the placeholder must never reach the run's declared outputs");
    }

    [Fact]
    public async Task A_map_branch_whose_recovery_sidecar_never_committed_refuses_to_reduce_the_redaction_marker()
    {
        var key = "sp-" + Guid.NewGuid().ToString("N");
        SuspendProbeNode.Reset(key);

        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        await SeedSecretAsync(teamId, userId, Sentinel);

        var workflowId = await CreateWorkflowAsync(teamId, userId, SecretEchoingSuspendingMapDefinition(key));
        var runId = await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, teamId, payloadJson: """{ "things": ["a", "b"] }""");

        await RunEngineAsync(runId, sidecarSaveFails: false);   // both branches park

        // Branch a resumes and completes its terminal while the sidecar write throws → the branch terminal row is
        // redacted with no recoverable original. Branch b re-suspends, so the map must REPLAY branch a next walk.
        (await ResolveBranchAsync(runId, key, "a", "RES-a")).ShouldBeTrue();
        await RunEngineAsync(runId, sidecarSaveFails: true);

        using (var mid = _fixture.BeginScope())
        {
            var db = mid.Resolve<CodeSpaceDbContext>();
            var leaf = await db.WorkflowRunNode.AsNoTracking().SingleAsync(n => n.RunId == runId && n.NodeId == "leaf" && n.IterationKey == "map#0");
            leaf.Status.ShouldBe(NodeStatus.Success, "precondition: branch a's terminal settled, so the next walk replays it instead of re-running it");
            leaf.OutputsJson.ShouldContain(PersistenceSecretRedactor.Marker, Case.Sensitive);
            (await db.WorkflowRunSensitiveRecordPayload.AsNoTracking().AnyAsync(p => p.RecordId == leaf.RecordId))
                .ShouldBeFalse("precondition: branch a's encrypted recovery sidecar never committed");
        }

        (await ResolveBranchAsync(runId, key, "b", "RES-b")).ShouldBeTrue();
        await RunEngineAsync(runId, sidecarSaveFails: false);

        using var verify = _fixture.BeginScope();
        var verifyDb = verify.Resolve<CodeSpaceDbContext>();

        var run = await verifyDb.WorkflowRun.AsNoTracking().SingleAsync(r => r.Id == runId);
        run.Status.ShouldBe(WorkflowRunStatus.Failure, "map replay must refuse a settled branch whose original is gone rather than reduce the placeholder");
        run.Error.ShouldNotBeNull();

        var mapRow = await verifyDb.WorkflowRunNode.AsNoTracking().SingleOrDefaultAsync(n => n.RunId == runId && n.NodeId == "map" && n.IterationKey == "");
        (mapRow?.Status == NodeStatus.Success).ShouldBeFalse("the map must not settle Success over a branch result that is only a placeholder");
    }

    [Fact]
    public async Task A_redacted_node_with_a_committed_sidecar_still_recovers_its_exact_original_on_resume()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        await SeedSecretAsync(teamId, userId, Sentinel);

        var workflowId = await CreateWorkflowAsync(teamId, userId, EchoThenGateDefinition());
        var runId = await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, teamId);

        await RunEngineAsync(runId, sidecarSaveFails: false);
        (await ApproveAsync(runId, teamId, userId)).ShouldBeTrue();
        await RunEngineAsync(runId, sidecarSaveFails: false);

        using var verify = _fixture.BeginScope();
        var db = verify.Resolve<CodeSpaceDbContext>();

        (await db.WorkflowRun.AsNoTracking().SingleAsync(r => r.Id == runId)).Status.ShouldBe(WorkflowRunStatus.Success);

        var after = await db.WorkflowRunNode.AsNoTracking().SingleAsync(n => n.RunId == runId && n.NodeId == "after" && n.IterationKey == "");
        after.OutputsJson.ShouldContain(PersistenceSecretRedactor.Marker, Case.Sensitive, "the public projection of the downstream node stays masked");
        after.OutputsJson.ShouldNotContain(Sentinel, Case.Sensitive);

        var recovered = await verify.Resolve<IWorkflowSensitivePayloadStore>().ReadNodeOutputsAsync(after.RecordId, runId, teamId, CancellationToken.None);
        recovered!["value"].GetString().ShouldBe("prefix-" + Sentinel, "the resumed downstream node received the EXACT original, recovered from the sidecar");
    }

    [Fact]
    public async Task A_node_with_nothing_to_redact_resumes_straight_from_the_ledger_with_no_sidecar()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);

        var workflowId = await CreateWorkflowAsync(teamId, userId, EchoThenGateDefinition(secretRef: false));
        var runId = await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, teamId);

        await RunEngineAsync(runId, sidecarSaveFails: false);
        (await ApproveAsync(runId, teamId, userId)).ShouldBeTrue();
        await RunEngineAsync(runId, sidecarSaveFails: false);

        using var verify = _fixture.BeginScope();
        var db = verify.Resolve<CodeSpaceDbContext>();

        (await db.WorkflowRun.AsNoTracking().SingleAsync(r => r.Id == runId)).Status.ShouldBe(WorkflowRunStatus.Success, "a run with nothing to redact is untouched by the recovery guard");

        var emit = await db.WorkflowRunNode.AsNoTracking().SingleAsync(n => n.RunId == runId && n.NodeId == "emit" && n.IterationKey == "");
        (await db.WorkflowRunSensitiveRecordPayload.AsNoTracking().AnyAsync(p => p.RecordId == emit.RecordId))
            .ShouldBeFalse("no secret was redacted, so the fast path writes no sidecar at all");

        var after = await db.WorkflowRunNode.AsNoTracking().SingleAsync(n => n.RunId == runId && n.NodeId == "after" && n.IterationKey == "");
        JsonDocument.Parse(after.OutputsJson).RootElement.GetProperty("value").GetString()
            .ShouldBe("prefix-plain", "the downstream node resumed from the ledger row itself — the untouched fast path");
    }

    // ─── Helpers ────────────────────────────────────────────────────────────────

    /// <summary>Stands in for the sidecar store whose SAVE cannot commit — the exact fault the engine's transaction-failure arm exists to survive. Reads answer null, matching a sidecar that was never written.</summary>
    private sealed class SidecarSaveFailsStore : IWorkflowSensitivePayloadStore
    {
        public Task SaveNodeOutputsAsync(Guid recordId, Guid runId, Guid teamId, IReadOnlyDictionary<string, JsonElement> outputs, CancellationToken cancellationToken) =>
            throw new InvalidOperationException($"simulated sidecar write failure for record {recordId}");

        public Task<IReadOnlyDictionary<string, JsonElement>?> ReadNodeOutputsAsync(Guid recordId, Guid runId, Guid teamId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyDictionary<string, JsonElement>?>(null);
    }

    private async Task SeedSecretAsync(Guid teamId, Guid userId, string value)
    {
        using var setup = _fixture.BeginScope();
        await setup.Resolve<IVariableService>().SetAsync(VariableScope.Team, teamId, teamId, "API_KEY", VariableValueType.Secret,
            JsonDocument.Parse(JsonSerializer.Serialize(value)).RootElement.Clone(), null, userId, CancellationToken.None);
    }

    private async Task RunEngineAsync(Guid runId, bool sidecarSaveFails)
    {
        using var scope = sidecarSaveFails
            ? _fixture.BeginScope(builder => builder.RegisterInstance(new SidecarSaveFailsStore()).As<IWorkflowSensitivePayloadStore>())
            : _fixture.BeginScope();

        await scope.Resolve<IWorkflowEngine>().ExecuteRunAsync(runId, CancellationToken.None);
    }

    private async Task<bool> ApproveAsync(Guid runId, Guid teamId, Guid userId)
    {
        using var scope = _fixture.BeginScopeAs(userId, teamId, Roles.Admin);
        return await scope.Resolve<IMediator>().Send(new ResumeRunCommand { RunId = runId, Approved = true, Comment = "continue" });
    }

    private async Task<bool> ResolveBranchAsync(Guid runId, string key, string element, string summary)
    {
        using var scope = _fixture.BeginScope();
        var waitId = await scope.Resolve<CodeSpaceDbContext>().WorkflowRunWait.AsNoTracking()
            .Where(w => w.RunId == runId && w.Token == $"{key}::{element}" && w.Status == WorkflowWaitStatuses.Pending)
            .Select(w => w.Id).SingleAsync();

        return await scope.Resolve<Core.Services.Workflows.Engine.IWorkflowResumeService>()
            .ResumeWaitAsync(runId, waitId, JsonSerializer.Serialize(new { summary }), CancellationToken.None);
    }

    private async Task<Guid> CreateWorkflowAsync(Guid teamId, Guid userId, WorkflowDefinition definition)
    {
        using var scope = _fixture.BeginScopeAs(userId, teamId, Roles.Admin);
        return await scope.Resolve<IMediator>().Send(new CreateWorkflowCommand
        {
            Name = "redact-recovery-" + Guid.NewGuid().ToString("N")[..8],
            Description = null,
            Definition = definition,
            Activations = new List<WorkflowActivationInput>(),
            Enabled = true,
        });
    }

    // start → emit (echoes a secret-bearing value) → gate (parks) → after (reads emit's output) → end.
    private static WorkflowDefinition EchoThenGateDefinition(bool secretRef = true) => new()
    {
        SchemaVersion = 1,
        Nodes = new List<NodeDefinition>
        {
            new() { Id = "start", TypeKey = "trigger.manual", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
            new() { Id = "emit", TypeKey = JsonEmitNode.Key, Config = WorkflowsTestSeed.EmptyJson(),
                    Inputs = WorkflowsTestSeed.Json(secretRef ? """{ "value": "prefix-{{team.API_KEY}}" }""" : """{ "value": "prefix-plain" }""") },
            new() { Id = "gate", TypeKey = "flow.wait_approval", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
            new() { Id = "after", TypeKey = JsonEmitNode.Key, Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.Json("""{ "value": "{{nodes.emit.outputs.value}}" }""") },
            new() { Id = "end", TypeKey = "builtin.terminal", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.Json("""{ "status": "ok" }""") },
        },
        Edges = new List<EdgeDefinition>
        {
            new() { From = "start", To = "emit" },
            new() { From = "emit", To = "gate" },
            new() { From = "gate", To = "after" },
            new() { From = "after", To = "end" },
        },
    };

    // manual → map(items={{trigger.things}}; body: ms → park[suspends] → leaf[echoes a secret-bearing value]) → end.
    private static WorkflowDefinition SecretEchoingSuspendingMapDefinition(string key) => new()
    {
        SchemaVersion = 1,
        Nodes = new List<NodeDefinition>
        {
            new() { Id = "start", TypeKey = "trigger.manual", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
            new() { Id = "map", TypeKey = "flow.map", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.Json("""{ "items": "{{trigger.things}}" }""") },
            new() { Id = "ms", TypeKey = "flow.map_start", ParentId = "map", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
            new() { Id = "park", TypeKey = SuspendProbeNode.Key, ParentId = "map", Config = WorkflowsTestSeed.EmptyJson(),
                    Inputs = WorkflowsTestSeed.Json("""{ "key": "__KEY__", "item": "{{item}}" }""".Replace("__KEY__", key)) },
            new() { Id = "leaf", TypeKey = JsonEmitNode.Key, ParentId = "map", Config = WorkflowsTestSeed.EmptyJson(),
                    Inputs = WorkflowsTestSeed.Json("""{ "value": "prefix-{{team.API_KEY}}", "summary": "{{nodes.park.outputs.summary}}" }""") },
            new() { Id = "end", TypeKey = "builtin.terminal", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.Json("""{ "status": "ok" }""") },
        },
        Edges = new List<EdgeDefinition>
        {
            new() { From = "start", To = "map" },
            new() { From = "map", To = "end" },
            new() { From = "ms", To = "park" },
            new() { From = "park", To = "leaf" },
        },
    };
}
