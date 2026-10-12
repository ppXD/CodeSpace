using System.Net;
using System.Text;
using System.Text.Json;
using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Credentials;
using CodeSpace.Core.Services.Workflows.Engine;
using CodeSpace.Core.Services.Workflows.Llm;
using CodeSpace.Core.Services.Workflows.Llm.OpenAi;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.IntegrationTests.Workflows.Infrastructure;
using CodeSpace.Messages.Commands.Workflows;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Dtos.Workflows;
using CodeSpace.Messages.Enums;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Shouldly;

namespace CodeSpace.IntegrationTests.Workflows;

/// <summary>
/// A model gateway that ECHOES the key it rejected (<c>Invalid API key provided: sk-…</c>) must not get that key into
/// anything the run persists. Drives the real engine, the real <see cref="OpenAiClient"/> over real HTTP to a loopback
/// gateway, and real Postgres; then scans every row the run wrote — node, interaction and external-call failure records,
/// the park marker, the run row, every table — for the key. A terminal 401 (the node fails) and a 429 (the node parks,
/// holding the fault text in its wait), and the production named-client pipeline
/// (<see cref="LlmHttpClientRegistration.AddLlmHttpClients"/>) beside the fixture's.
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public sealed class LlmGatewayKeyEchoFlowTests
{
    private readonly PostgresFixture _fixture;

    public LlmGatewayKeyEchoFlowTests(PostgresFixture fixture) { _fixture = fixture; }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, false, WorkflowRunRecordTypes.NodeFailed)]
    [InlineData(HttpStatusCode.TooManyRequests, false, WorkflowRunRecordTypes.NodeSuspended)]
    [InlineData(HttpStatusCode.Unauthorized, true, WorkflowRunRecordTypes.NodeFailed)]
    public async Task A_gateway_echoing_the_key_leaves_no_key_in_any_failure_record(HttpStatusCode status, bool productionPipeline, string nodeOutcome)
    {
        var key = $"sk-ECHO-FLOW-{Guid.NewGuid():N}";
        await using var gateway = LoopbackResponder.Start(head => EchoTheKey(head, status));
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture, inProcessPool: false);
        await SeedGatewayCredentialAsync(teamId, key, $"http://127.0.0.1:{gateway.Port}/v1");

        var runId = await WorkflowsTestSeed.SeedManualRunAsync(_fixture, await CreateLlmWorkflowAsync(teamId, userId), teamId);
        await RunEngineAsync(runId, productionPipeline);

        gateway.Requests.ShouldContain(r => r.Contains(key), "fixture check: the gateway was presented the key, so its echo carried it");
        var records = await RecordsAsync(runId);
        records.Select(r => r.RecordType).ShouldContain(nodeOutcome, $"fixture check: a {(int)status} reaches the node's {nodeOutcome} path");
        records.Select(r => r.RecordType).ShouldContain(WorkflowRunRecordTypes.InteractionFailed);
        records.Select(r => r.RecordType).ShouldContain(WorkflowRunRecordTypes.ExternalCallFailed);

        records.Where(r => r.PayloadJson.Contains(key)).Select(r => r.RecordType).ShouldBeEmpty("no record may carry the echoed key");
        records.Single(r => r.RecordType == WorkflowRunRecordTypes.InteractionFailed).PayloadJson.ShouldContain("Invalid API key provided: ***", Case.Sensitive, "the gateway's own words survive around the struck key");
        (await RowsContainingAsync(key)).ShouldBeEmpty("no table may hold the echoed key in plaintext — the park marker and the run row included");
    }

    private static string EchoTheKey(string head, HttpStatusCode status)
    {
        var presented = head.Split("\r\n").FirstOrDefault(line => line.StartsWith("Authorization:", StringComparison.OrdinalIgnoreCase))?.Split(' ').LastOrDefault() ?? "";
        var body = JsonSerializer.Serialize(new { error = new { message = $"Invalid API key provided: {presented}", type = "invalid_request_error" } });

        return $"HTTP/1.1 {(int)status} {status}\r\nContent-Type: application/json\r\nRetry-After: 0\r\nContent-Length: {Encoding.UTF8.GetByteCount(body)}\r\nConnection: close\r\n\r\n{body}";
    }

    private async Task SeedGatewayCredentialAsync(Guid teamId, string key, string baseUrl)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var credentialId = Guid.NewGuid();

        db.ModelCredential.Add(new ModelCredential { Id = credentialId, TeamId = teamId, Provider = "OpenAI", DisplayName = "echoing gateway", EncryptedApiKey = scope.Resolve<IPayloadEncryptor>().Encrypt(key), BaseUrl = baseUrl, Status = CredentialStatus.Active, CreatedBy = SystemUsers.SeederId, LastModifiedBy = SystemUsers.SeederId });
        db.ModelCredentialModel.Add(new ModelCredentialModel { Id = Guid.NewGuid(), ModelCredentialId = credentialId, ModelId = "gpt-echo", Source = ModelSource.Manual, Enabled = true });
        await db.SaveChangesAsync();
    }

    private async Task<Guid> CreateLlmWorkflowAsync(Guid teamId, Guid userId)
    {
        using var scope = _fixture.BeginScopeAs(userId, teamId, Roles.Admin);

        return await scope.Resolve<IMediator>().Send(new CreateWorkflowCommand
        {
            Name = $"echo-{Guid.NewGuid():N}"[..20], Description = null, Activations = new List<WorkflowActivationInput>(), Enabled = true,
            Definition = new WorkflowDefinition
            {
                SchemaVersion = 1,
                Nodes = new List<NodeDefinition>
                {
                    new() { Id = "start", TypeKey = "trigger.manual", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
                    new() { Id = "llm", TypeKey = "llm.complete", Config = WorkflowsTestSeed.Json("""{ "provider": "OpenAI", "model": "gpt-echo" }"""), Inputs = WorkflowsTestSeed.Json("""{ "userPrompt": "hello" }""") },
                    new() { Id = "end", TypeKey = "builtin.terminal", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
                },
                Edges = new List<EdgeDefinition> { new() { From = "start", To = "llm" }, new() { From = "llm", To = "end" } },
            },
        });
    }

    /// <summary>The fixture's registered OpenAI client, or — for the production pipeline — the real <see cref="OpenAiClient"/> over <see cref="LlmHttpClientRegistration.AddLlmHttpClients"/> (accounting handler + resilience), wrapped in the same recording decorator production uses.</summary>
    private async Task RunEngineAsync(Guid runId, bool productionPipeline)
    {
        if (!productionPipeline)
        {
            using var fixtureScope = _fixture.BeginScope();
            await fixtureScope.Resolve<IWorkflowEngine>().ExecuteRunAsync(runId, CancellationToken.None);
            return;
        }

        var collection = new ServiceCollection();
        collection.AddLogging();
        collection.AddLlmHttpClients();
        await using var services = collection.BuildServiceProvider();
        var registry = new LLMClientRegistry([new RecordingStreamingStructuredLLMClientDecorator(new OpenAiClient(services.GetRequiredService<IHttpClientFactory>()))]);

        using var scope = _fixture.BeginScope(builder => builder.RegisterInstance(registry).As<ILLMClientRegistry>());
        await scope.Resolve<IWorkflowEngine>().ExecuteRunAsync(runId, CancellationToken.None);
    }

    private async Task<List<WorkflowRunRecord>> RecordsAsync(Guid runId)
    {
        using var scope = _fixture.BeginScope();

        return await scope.Resolve<CodeSpaceDbContext>().WorkflowRunRecord.AsNoTracking().Where(r => r.RunId == runId).OrderBy(r => r.Sequence).ToListAsync();
    }

    /// <summary>Every base table, every row, as text — the key must be nowhere, not only in the record types this test names.</summary>
    private async Task<List<string>> RowsContainingAsync(string needle)
    {
        var hits = new List<string>();
        await using var connection = new NpgsqlConnection(_fixture.ConnectionString);
        await connection.OpenAsync();

        foreach (var table in await BaseTablesAsync(connection))
        {
            await using var query = new NpgsqlCommand($"SELECT count(*) FROM {table} x WHERE x::text LIKE @needle", connection);
            query.Parameters.AddWithValue("needle", $"%{needle}%");

            if ((long)(await query.ExecuteScalarAsync())! > 0) hits.Add(table);
        }

        return hits;
    }

    private static async Task<List<string>> BaseTablesAsync(NpgsqlConnection connection)
    {
        var tables = new List<string>();
        await using var list = new NpgsqlCommand("SELECT quote_ident(table_schema) || '.' || quote_ident(table_name) FROM information_schema.tables WHERE table_type = 'BASE TABLE' AND table_schema NOT IN ('pg_catalog', 'information_schema')", connection);
        await using var reader = await list.ExecuteReaderAsync();

        while (await reader.ReadAsync()) tables.Add(reader.GetString(0));

        return tables;
    }
}
