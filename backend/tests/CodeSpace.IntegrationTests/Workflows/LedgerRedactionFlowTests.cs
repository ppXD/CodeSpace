using System.Text.Json;
using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Services.Variables;
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
/// End-to-end proof of the secret-leak redaction guarantee. The unit-test suite
/// (<see cref="UnitTests.Workflows.PayloadRedactorTests"/>) pins the redactor's per-key
/// policy in isolation; this suite plugs the redactor into the real engine running against
/// real Postgres and asserts the same guarantee survives the persistence layer.
///
/// <para>The contract being protected: <b>no plaintext secret value ever lands in any row
/// of <c>workflow_run_record.payload_json</c> for any run that resolved a Secret-typed
/// variable</b>. If this contract breaks, the run-detail UI / audit tooling / future log
/// exports would expose decrypted credentials to anyone with run-view permission.</para>
/// </summary>
[Collection(PostgresCollection.Name)]
public class LedgerRedactionFlowTests
{
    private readonly PostgresFixture _fixture;

    public LedgerRedactionFlowTests(PostgresFixture fixture) { _fixture = fixture; }

    private static JsonElement JsonString(string s) => JsonDocument.Parse(JsonSerializer.Serialize(s)).RootElement.Clone();

    [Fact]
    public async Task Resolved_secret_in_node_inputs_lands_redacted_in_ledger_not_plaintext()
    {
        // Sentinel value chosen long enough to be unmistakable in error messages.
        const string Sentinel = "sk-PROD-DO-NOT-LEAK-ABCDEFGH";
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);

        // Operator sets a Secret-typed team variable.
        using (var setup = _fixture.BeginScope())
        {
            await setup.Resolve<IVariableService>().SetAsync(
                VariableScope.Team, teamId, teamId, "API_KEY", VariableValueType.Secret,
                JsonString(Sentinel), null, userId, CancellationToken.None);
        }

        // Workflow: trigger → http.request whose Authorization header references the secret
        // → terminal (clean). The HTTP call points at an unreachable port so it fails fast,
        // but node.started has already been written to the ledger BEFORE the call attempt.
        var def = new WorkflowDefinition
        {
            SchemaVersion = 1,
            Nodes = new List<NodeDefinition>
            {
                new() { Id = "start",   TypeKey = "trigger.pr.opened", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
                new() { Id = "call",    TypeKey = "http.request",       Config = WorkflowsTestSeed.EmptyJson(),
                        Inputs = WorkflowsTestSeed.Json("""
                            {
                                "url": "http://127.0.0.1:1/never-reached",
                                "method": "GET",
                                "headers": { "Authorization": "Bearer {{team.API_KEY}}" }
                            }
                            """) },
                new() { Id = "end",     TypeKey = "builtin.terminal",   Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
            },
            Edges = new List<EdgeDefinition>
            {
                new() { From = "start", To = "call" },
                new() { From = "call",  To = "end" },
            },
        };

        var workflowId = await CreateWorkflowAsync(teamId, userId, def);
        var runId = await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, teamId);
        await RunEngineAsync(runId);

        // ─── Assertions ─────────────────────────────────────────────────────
        using var verify = _fixture.BeginScope();
        var db = verify.Resolve<CodeSpaceDbContext>();

        // 1. The node.started record for the http.request node MUST carry the redaction marker.
        var nodeStarted = await db.WorkflowRunRecord.AsNoTracking()
            .Where(r => r.RunId == runId && r.NodeId == "call" && r.RecordType == "node.started")
            .SingleAsync();

        var startedPayload = JsonDocument.Parse(nodeStarted.PayloadJson).RootElement;
        var headers = startedPayload.GetProperty("inputs").GetProperty("headers");
        headers.GetProperty("Authorization").GetString()
            .ShouldBe("[REDACTED: team.API_KEY]",
                "Authorization header carrying {{team.API_KEY}} MUST land redacted in node.started.payload_json.inputs");

        // 2. SENTINEL ANTI-LEAK: no row in workflow_run_record for this run can carry the
        //    plaintext anywhere. This is the belt-and-suspenders guarantee — even if a future
        //    record type bypassed the redactor, this sweep would catch it.
        var allRecords = await db.WorkflowRunRecord.AsNoTracking()
            .Where(r => r.RunId == runId)
            .ToListAsync();

        foreach (var record in allRecords)
        {
            record.PayloadJson.Contains(Sentinel).ShouldBeFalse(
                $"Secret plaintext '{Sentinel}' appeared in workflow_run_record.payload_json for record_type='{record.RecordType}'. " +
                "The ledger MUST NOT contain decrypted secrets.");
        }

        // 3. The variable snapshot must also be free of plaintext (already enforced by the
        //    snapshot contract, re-asserted here as defence-in-depth).
        var snapshot = await db.WorkflowRunVariable.AsNoTracking()
            .Where(v => v.RunId == runId)
            .ToListAsync();

        foreach (var row in snapshot)
        {
            (row.ValuePlain ?? string.Empty).Contains(Sentinel).ShouldBeFalse(
                "Secret plaintext appeared in workflow_run_variable.value_plain — snapshot contract broken");
        }
    }

    [Fact]
    public async Task Non_secret_inputs_pass_through_to_ledger_unchanged()
    {
        // Negative control — a non-secret team variable referenced in node inputs MUST
        // appear in the ledger AS-IS, no false-positive redaction. Without this, operators
        // would see "[REDACTED: ...]" everywhere and lose debugging value.
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);

        using (var setup = _fixture.BeginScope())
        {
            await setup.Resolve<IVariableService>().SetAsync(
                VariableScope.Team, teamId, teamId, "BASE_URL", VariableValueType.String,
                JsonString("https://example.com"), null, userId, CancellationToken.None);
        }

        var def = new WorkflowDefinition
        {
            SchemaVersion = 1,
            Nodes = new List<NodeDefinition>
            {
                new() { Id = "start",   TypeKey = "trigger.pr.opened", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
                new() { Id = "call",    TypeKey = "http.request",       Config = WorkflowsTestSeed.EmptyJson(),
                        Inputs = WorkflowsTestSeed.Json("""
                            {
                                "url": "{{team.BASE_URL}}/will-fail",
                                "method": "GET"
                            }
                            """) },
                new() { Id = "end",     TypeKey = "builtin.terminal",   Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
            },
            Edges = new List<EdgeDefinition>
            {
                new() { From = "start", To = "call" },
                new() { From = "call",  To = "end" },
            },
        };

        var workflowId = await CreateWorkflowAsync(teamId, userId, def);
        var runId = await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, teamId);
        await RunEngineAsync(runId);

        using var verify = _fixture.BeginScope();
        var db = verify.Resolve<CodeSpaceDbContext>();

        var nodeStarted = await db.WorkflowRunRecord.AsNoTracking()
            .Where(r => r.RunId == runId && r.NodeId == "call" && r.RecordType == "node.started")
            .SingleAsync();

        var url = JsonDocument.Parse(nodeStarted.PayloadJson).RootElement
            .GetProperty("inputs").GetProperty("url").GetString();

        url.ShouldBe("https://example.com/will-fail",
            "Non-secret variable resolution MUST pass through to the ledger unchanged — no false-positive redaction on plain String type");
    }

    // ─── Helpers ────────────────────────────────────────────────────────────────

    private async Task<Guid> CreateWorkflowAsync(Guid teamId, Guid userId, WorkflowDefinition def)
    {
        using var scope = _fixture.BeginScopeAs(userId, teamId, Roles.Admin);
        var mediator = scope.Resolve<IMediator>();
        return await mediator.Send(new CreateWorkflowCommand
        {
            Name = "redact-" + Guid.NewGuid().ToString("N")[..8],
            Description = null,
            Definition = def,
            Activations = new List<WorkflowActivationInput>(),
            Enabled = true,
        });
    }

    private async Task RunEngineAsync(Guid runId)
    {
        using var scope = _fixture.BeginScope();
        await scope.Resolve<Core.Services.Workflows.Engine.IWorkflowEngine>()
            .ExecuteRunAsync(runId, CancellationToken.None);
    }
}
