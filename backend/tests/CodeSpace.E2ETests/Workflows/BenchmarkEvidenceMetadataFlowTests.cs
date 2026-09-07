using System.Data.Common;
using System.Text;
using System.Text.Json;
using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Agents.Eval.Benchmark;
using CodeSpace.Core.Services.Workflows.Artifacts;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.IntegrationTests.Workflows.Infrastructure;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Agents.Benchmark;
using CodeSpace.Messages.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;
using Shouldly;

namespace CodeSpace.E2ETests.Workflows;

[Collection(PostgresCollection.Name)]
[Trait("Category", "E2E")]
[Trait("Surface", "Engine")]
public sealed class BenchmarkEvidenceMetadataFlowTests(PostgresFixture fixture) : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "cs-benchmark-metadata-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Large_unrelated_task_and_result_roots_never_cross_the_export_read_boundary()
    {
        var recorder = new ReadRecorder();
        var cell = await ExportAsync("{\"model\":\"fixture-model\",\"exitReason\":\"completed\",\"reviseRounds\":2,\"tokenUsage\":{\"inputTokens\":10,\"outputTokens\":7}}", recorder);
        cell.GetProperty("finalAttempt").GetProperty("reportedUsage").GetProperty("inputTokens").GetInt32().ShouldBe(10);
        cell.GetProperty("discoveredCandidates").GetArrayLength().ShouldBe(2);
        recorder.Commands.Count.ShouldBeGreaterThanOrEqualTo(2, "exercise both exact final-row and candidate reads");
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        foreach (var recorded in recorder.Commands)
        {
            await using var command = new NpgsqlCommand(recorded.Sql, connection);
            command.Parameters.AddRange(recorded.Parameters.Select(p => p.Clone()).ToArray());
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                var transported = 0;
                for (var i = 0; i < reader.FieldCount; i++)
                    if (reader.GetValue(i) is string value) transported += Encoding.UTF8.GetByteCount(value);
                transported.ShouldBeLessThan(12_288, "replay the actual consumer SQL against multi-MiB task and summary roots; a row-count cap is not a byte bound");
            }
        }
    }

    [Theory]
    [InlineData("{\"tokenUsage\":{\"inputTokens\":\"10\",\"outputTokens\":7}}")]
    [InlineData("{\"tokenUsage\":{\"inputTokens\":10}}")]
    [InlineData("{\"tokenUsage\":{\"inputTokens\":2147483648,\"outputTokens\":7}}")]
    [InlineData("{\"tokenUsage\":{\"inputTokens\":-1,\"outputTokens\":7}}")]
    [InlineData("{\"tokenUsage\":{\"inputTokens\":1.25,\"outputTokens\":7}}")]
    [InlineData("{\"tokenUsage\":[]}")]
    [InlineData("[]")]
    public async Task Malformed_usage_never_becomes_reported_zero_or_a_parse_failure_for_the_entire_cell(string json)
    {
        var cell = await ExportAsync(json);
        cell.GetProperty("discoveryStatus").GetString().ShouldBe("available");
        var attempt = cell.GetProperty("finalAttempt");
        attempt.GetProperty("reportedUsage").ValueKind.ShouldBe(JsonValueKind.Null);
        attempt.GetProperty("usageAvailability").GetString().ShouldBe("unknown-invalid-shape");
        cell.GetProperty("finalGradePassed").GetBoolean().ShouldBeFalse();
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(int.MaxValue, int.MaxValue)]
    public async Task Representable_usage_endpoints_remain_exact_reported_values(int input, int output)
    {
        var cell = await ExportAsync(JsonSerializer.Serialize(new { tokenUsage = new { inputTokens = input, outputTokens = output } }));
        var attempt = cell.GetProperty("finalAttempt");
        attempt.GetProperty("usageAvailability").GetString().ShouldBe("reported");
        attempt.GetProperty("reportedUsage").GetProperty("inputTokens").GetInt32().ShouldBe(input);
        attempt.GetProperty("reportedUsage").GetProperty("outputTokens").GetInt32().ShouldBe(output);
    }

    [Fact]
    public async Task Exact_UTF8_field_bounds_allow_complete_unicode_values()
    {
        var model = string.Concat(Enumerable.Repeat("🔒", 1024));
        var exit = string.Concat(Enumerable.Repeat("🔑", 512));
        var cell = await ExportAsync(JsonSerializer.Serialize(new { model, exitReason = exit }));
        var attempt = cell.GetProperty("finalAttempt");
        attempt.GetProperty("modelAvailability").GetString().ShouldBe("available");
        attempt.GetProperty("observedModelFingerprint").GetString().ShouldBe(BenchmarkEvidenceExport.Hash(Encoding.UTF8.GetBytes(model))[..16]);
        attempt.GetProperty("exitReasonAvailability").GetString().ShouldBe("available");
        attempt.GetProperty("exitReason").GetString().ShouldBe(exit);
        cell.GetProperty("gradeEvidence").GetProperty("availability").GetString().ShouldBe("available");
    }

    [Fact]
    public async Task Oversized_model_and_exit_fields_are_withheld_without_leaking_a_truncated_secret()
    {
        var cell = await ExportAsync(JsonSerializer.Serialize(new { model = new string('m', 5000), exitReason = new string('x', 9000) }));
        var attempt = cell.GetProperty("finalAttempt");
        attempt.GetProperty("observedModelFingerprint").ValueKind.ShouldBe(JsonValueKind.Null);
        attempt.GetProperty("exitReason").ValueKind.ShouldBe(JsonValueKind.Null);
        attempt.GetProperty("modelAvailability").GetString().ShouldBe("unknown-field-exceeds-bound");
        attempt.GetProperty("exitReasonAvailability").GetString().ShouldBe("unknown-field-exceeds-bound");
        var grade = cell.GetProperty("gradeEvidence");
        grade.GetProperty("availability").GetString().ShouldBe("unknown-model-exceeds-export-bound");
        grade.GetProperty("textFile").ValueKind.ShouldBe(JsonValueKind.Null);
        cell.GetProperty("finalGradeDetail").ValueKind.ShouldBe(JsonValueKind.Null);
        foreach (var path in Directory.GetFiles(_directory)) File.ReadAllText(path).ShouldNotContain(new string('m', 100));
    }

    private async Task<JsonElement> ExportAsync(string resultJson, ReadRecorder? recorder = null)
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(fixture);
        using var parent = fixture.BeginScopeAs(userId, teamId);
        using var scope = parent.BeginLifetimeScope(builder =>
        {
            builder.RegisterInstance(new LargeMetadataRunner(parent, resultJson)).As<IBenchmarkRunner>();
            if (recorder is not null)
            {
                var options = new DbContextOptionsBuilder<CodeSpaceDbContext>().UseNpgsql(fixture.ConnectionString).UseSnakeCaseNamingConvention().AddInterceptors(recorder).Options;
                builder.RegisterInstance(options).As<DbContextOptions<CodeSpaceDbContext>>().SingleInstance();
            }
        });
        var request = new CorpusBenchmarkRequest { TeamId = teamId, Tasks = new[] { SeedBenchmarkCorpus.Tasks[0] with { Modes = new[] { BenchmarkMode.HarnessCli } } } };
        await BenchmarkEvidenceExport.RunAsync(scope, request, new BenchmarkEvidenceOptions(_directory, "metadata-test", new[] { "fixture-model" }), CancellationToken.None);
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(_directory, "cell-0001.json")));
        return document.RootElement.Clone();
    }

    public void Dispose() { if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true); }

    private sealed class LargeMetadataRunner(ILifetimeScope scope, string json) : IBenchmarkRunner
    {
        public async Task<BenchmarkResult> RunAsync(BenchmarkTask task, BenchmarkMode mode, BenchmarkExecutionContext context, CancellationToken cancellationToken)
        {
            var db = scope.Resolve<CodeSpaceDbContext>();
            Guid finalId = default;
            for (var i = 0; i < 2; i++)
            {
                var run = await scope.Resolve<IAgentRunService>().CreateAsync(new AgentTask { Goal = i == 0 ? "unrelated reviewer" : "final fixture", Harness = "codex-cli", WorkspaceDirectory = context.WorkspaceDirectory }, context.TeamId, null, null, "", cancellationToken);
                finalId = run.Id;
                // Large irrelevant roots stay in PostgreSQL. Keep malformed root arrays malformed.
                await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE agent_run SET task_jsonb = task_jsonb || jsonb_build_object('unrelated', repeat('t', 2097152)), result_jsonb = CASE WHEN jsonb_typeof({json}::jsonb) = 'object' THEN jsonb_build_object('status', 'Succeeded', 'exitReason', 'completed') || {json}::jsonb || jsonb_build_object('summary', repeat('s', 2097152)) ELSE {json}::jsonb END, status = 'Succeeded' WHERE id = {run.Id}", cancellationToken);
            }
            var artifactId = await scope.Resolve<IArtifactStore>().PutAsync(context.TeamId, Encoding.UTF8.GetBytes("grade detail fixture-model"), "text/plain", cancellationToken);
            return new BenchmarkResult { TaskId = task.Id, Mode = mode, AgentRunId = finalId, RunStatus = AgentRunStatus.Succeeded, Grade = new BenchmarkGrade { Passed = false, Detail = "tests-failed " + json, EvidenceArtifactId = artifactId }, McpFullCatalog = false };
        }
    }

    private sealed record RecordedRead(string Sql, NpgsqlParameter[] Parameters);
    private sealed class ReadRecorder : DbCommandInterceptor
    {
        public List<RecordedRead> Commands { get; } = [];
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("FROM agent_run", StringComparison.OrdinalIgnoreCase)) Commands.Add(new RecordedRead(command.CommandText, command.Parameters.Cast<NpgsqlParameter>().Select(p => p.Clone()).ToArray()));
            return ValueTask.FromResult(result);
        }
    }
}
