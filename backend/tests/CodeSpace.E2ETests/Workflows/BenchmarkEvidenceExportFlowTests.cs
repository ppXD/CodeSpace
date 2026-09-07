using System.Text;
using System.Text.Json;
using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Agents.Eval.Benchmark;
using CodeSpace.Core.Services.Workflows.Artifacts;
using CodeSpace.E2ETests.Infrastructure;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.IntegrationTests.Workflows.Infrastructure;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Agents.Benchmark;
using CodeSpace.Messages.Enums;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace CodeSpace.E2ETests.Workflows;

[Collection(PostgresCollection.Name)]
[Trait("Category", "E2E")]
[Trait("Surface", "Engine")]
public sealed class BenchmarkEvidenceExportFlowTests : IDisposable
{
    private const string Secret = "private-api-key-123456";
    private readonly PostgresFixture _fixture;
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "cs-benchmark-export-" + Guid.NewGuid().ToString("N"));

    public BenchmarkEvidenceExportFlowTests(PostgresFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task The_real_corpus_consumer_exports_a_failed_graders_bytes_before_its_workspace_is_deleted()
    {
        using var cli = new FakeCodexCli();
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        using var scope = _fixture.BeginScopeAs(userId, teamId);
        var task = SeedBenchmarkCorpus.Tasks[0] with { Modes = new[] { BenchmarkMode.HarnessCli }, Goal = "do the deterministic export fixture", TestCommand = new[] { "sh", "-c", "printf 'oracle detail " + Secret + "\\n' >&2; exit 1" } };
        var request = new CorpusBenchmarkRequest { Tasks = new[] { task }, TeamId = teamId };

        var result = await BenchmarkEvidenceExport.RunAsync(scope, request, Options(), CancellationToken.None);

        result.Results.ShouldHaveSingleItem().Grade.Passed.ShouldBeFalse();
        var cell = ReadCell();
        cell.GetProperty("finalGradePassed").GetBoolean().ShouldBeFalse();
        cell.GetProperty("firstOraclePassed").GetBoolean().ShouldBeFalse();
        cell.GetProperty("declaredRespawns").GetInt32().ShouldBe(0);
        cell.GetProperty("declaredAgentRunAttempts").GetInt32().ShouldBe(1);
        cell.GetProperty("attemptUnit").GetString().ShouldContain("provider requests are not independently enumerated");
        cell.GetProperty("finalAttempt").GetProperty("runId").GetGuid().ShouldBe(result.Results[0].AgentRunId!.Value);
        var grade = cell.GetProperty("gradeEvidence");
        grade.GetProperty("availability").GetString().ShouldBe("available");
        grade.GetProperty("partial").GetBoolean().ShouldBeFalse();
        var exported = File.ReadAllText(Path.Combine(_directory, grade.GetProperty("textFile")!.GetString()!));
        exported.ShouldContain("oracle detail ***");
        exported.ShouldNotContain(Secret);
        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(_directory, "manifest.json")));
        manifest.RootElement.GetProperty("declaredCells").GetArrayLength().ShouldBe(1);
        manifest.RootElement.GetProperty("finished").GetBoolean().ShouldBeTrue();
        var run = await scope.Resolve<CodeSpaceDbContext>().AgentRun.AsNoTracking().SingleAsync(r => r.Id == result.Results[0].AgentRunId);
        Directory.Exists(JsonSerializer.Deserialize<AgentTask>(run.TaskJson, AgentJson.Options)!.WorkspaceDirectory).ShouldBeFalse();
    }

    [Fact]
    public async Task Same_workspace_rows_do_not_become_causal_retry_attempts_or_an_invented_first_oracle()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        using var parent = _fixture.BeginScopeAs(userId, teamId);
        using var scope = parent.BeginLifetimeScope(b => b.RegisterInstance(new RecordedFixtureRunner(parent, respawns: 1)).As<IBenchmarkRunner>());
        var result = await BenchmarkEvidenceExport.RunAsync(scope, Request(teamId), Options(), CancellationToken.None);
        var cell = ReadCell();
        cell.GetProperty("declaredRespawns").GetInt32().ShouldBe(1);
        cell.GetProperty("attemptLineage").GetString().ShouldBe("unknown-no-durable-retry-link");
        cell.GetProperty("firstOraclePassed").ValueKind.ShouldBe(JsonValueKind.Null);
        cell.GetProperty("firstCompletionSucceeded").ValueKind.ShouldBe(JsonValueKind.Null);
        cell.GetProperty("discoveredCandidates").GetArrayLength().ShouldBe(2);
        cell.GetProperty("finalAttempt").GetProperty("runId").GetGuid().ShouldBe(result.Results[0].AgentRunId!.Value);
        cell.GetProperty("reportedAggregateUsage").GetProperty("inputTokens").GetInt32().ShouldBe(10);
        cell.GetProperty("aggregateUsageCompleteness").GetString().ShouldBe("unknown");
    }

    [Fact]
    public async Task Missing_grade_artifact_remains_explicitly_unknown_and_does_not_change_a_failed_grade()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        using var parent = _fixture.BeginScopeAs(userId, teamId);
        using var scope = parent.BeginLifetimeScope(b => b.RegisterInstance(new RecordedFixtureRunner(parent, missingArtifact: true)).As<IBenchmarkRunner>());
        var result = await BenchmarkEvidenceExport.RunAsync(scope, Request(teamId), Options(), CancellationToken.None);
        result.Results.ShouldHaveSingleItem().Grade.Passed.ShouldBeFalse();
        var grade = ReadCell().GetProperty("gradeEvidence");
        grade.GetProperty("availability").GetString().ShouldBe("MetadataMissing");
        grade.GetProperty("textFile").ValueKind.ShouldBe(JsonValueKind.Null);
        grade.GetProperty("exportedSha256").ValueKind.ShouldBe(JsonValueKind.Null);
    }

    [Fact]
    public async Task A_failed_fixture_stage_remains_an_unexported_cell_in_the_frozen_manifest()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        using var scope = _fixture.BeginScopeAs(userId, teamId);
        var request = Request(teamId) with { Tasks = new[] { SeedBenchmarkCorpus.Tasks[0] with { FixtureRef = "unavailable-fixture", Modes = new[] { BenchmarkMode.HarnessCli } } } };
        var result = await BenchmarkEvidenceExport.RunAsync(scope, request, Options(), CancellationToken.None);
        result.Results.ShouldBeEmpty();
        result.Errored.ShouldHaveSingleItem();
        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(_directory, "manifest.json")));
        manifest.RootElement.GetProperty("declaredCells").GetArrayLength().ShouldBe(1);
        manifest.RootElement.GetProperty("unexportedCells").GetArrayLength().ShouldBe(1);
        manifest.RootElement.GetProperty("unexportedCells")[0].GetProperty("status").GetString().ShouldBe("unknown-unexported");
        manifest.RootElement.GetProperty("suiteVersion").GetString().ShouldBe(result.SuiteVersion);
    }

    [Fact]
    public async Task Foreign_team_run_and_artifact_ids_cannot_leak_through_the_diagnostic_exporter()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var (otherTeamId, otherUserId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        using var other = _fixture.BeginScopeAs(otherUserId, otherTeamId);
        var foreignRun = await other.Resolve<IAgentRunService>().CreateAsync(new AgentTask { Goal = "foreign-private-goal", Harness = "codex-cli" }, otherTeamId, null, null, "", CancellationToken.None);
        var foreignArtifact = await other.Resolve<IArtifactStore>().PutAsync(otherTeamId, Encoding.UTF8.GetBytes("foreign-private-output"), "text/plain", CancellationToken.None);
        var falseClaim = new BenchmarkResult { TaskId = SeedBenchmarkCorpus.Tasks[0].Id, Mode = BenchmarkMode.HarnessCli, AgentRunId = foreignRun.Id, RunStatus = AgentRunStatus.Succeeded, Grade = new BenchmarkGrade { Passed = false, Detail = "failed", EvidenceArtifactId = foreignArtifact }, McpFullCatalog = false };
        using var parent = _fixture.BeginScopeAs(userId, teamId);
        using var scope = parent.BeginLifetimeScope(b => b.RegisterInstance(new FixedResultRunner(falseClaim)).As<IBenchmarkRunner>());

        await BenchmarkEvidenceExport.RunAsync(scope, Request(teamId), Options(), CancellationToken.None);

        var cell = ReadCell();
        cell.GetProperty("finalAttempt").ValueKind.ShouldBe(JsonValueKind.Null);
        cell.GetProperty("firstOraclePassed").ValueKind.ShouldBe(JsonValueKind.Null);
        cell.GetProperty("gradeEvidence").GetProperty("availability").GetString().ShouldBe("MetadataMissing");
        foreach (var file in Directory.GetFiles(_directory)) File.ReadAllText(file).ShouldNotContain("foreign-private");
    }

    private BenchmarkEvidenceOptions Options() => new(_directory, "deterministic-test", new[] { Secret, "private-model", "https://private-endpoint.invalid" });
    private static CorpusBenchmarkRequest Request(Guid teamId) => new() { TeamId = teamId, Tasks = new[] { SeedBenchmarkCorpus.Tasks[0] with { Modes = new[] { BenchmarkMode.HarnessCli } } } };
    private JsonElement ReadCell() => JsonDocument.Parse(File.ReadAllText(Path.Combine(_directory, "cell-0001.json"))).RootElement.Clone();
    public void Dispose() { if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true); }

    private sealed class FixedResultRunner(BenchmarkResult result) : IBenchmarkRunner
    {
        public Task<BenchmarkResult> RunAsync(BenchmarkTask task, BenchmarkMode mode, BenchmarkExecutionContext context, CancellationToken cancellationToken) => Task.FromResult(result);
    }

    private sealed class RecordedFixtureRunner : IBenchmarkRunner
    {
        private readonly ILifetimeScope _scope;
        private readonly int _respawns;
        private readonly bool _missingArtifact;
        public RecordedFixtureRunner(ILifetimeScope scope, int respawns = 0, bool missingArtifact = false) { _scope = scope; _respawns = respawns; _missingArtifact = missingArtifact; }
        public async Task<BenchmarkResult> RunAsync(BenchmarkTask task, BenchmarkMode mode, BenchmarkExecutionContext context, CancellationToken cancellationToken)
        {
            var service = _scope.Resolve<IAgentRunService>();
            var payload = new AgentTask { Goal = "export fixture", Harness = "codex-cli", WorkspaceDirectory = context.WorkspaceDirectory };
            if (_respawns > 0) await service.CreateAsync(payload with { Goal = "an unrelated reviewer sharing cwd" }, context.TeamId, null, null, "", cancellationToken);
            var run = await service.CreateAsync(payload, context.TeamId, null, null, "", cancellationToken);
            var usage = new AgentTokenUsage { InputTokens = 10, OutputTokens = 7 };
            var result = new AgentRunResult { Status = AgentRunStatus.Succeeded, ExitReason = "completed", Model = "private-model", TokenUsage = usage };
            await _scope.Resolve<CodeSpaceDbContext>().AgentRun.Where(r => r.Id == run.Id).ExecuteUpdateAsync(s => s.SetProperty(r => r.Status, AgentRunStatus.Succeeded).SetProperty(r => r.ResultJson, JsonSerializer.Serialize(result, AgentJson.Options)), cancellationToken);
            var artifactId = _missingArtifact ? Guid.NewGuid() : await _scope.Resolve<IArtifactStore>().PutAsync(context.TeamId, Encoding.UTF8.GetBytes("bounded grader " + Secret), "text/plain", cancellationToken);
            return new BenchmarkResult { TaskId = task.Id, Mode = mode, AgentRunId = run.Id, RunStatus = AgentRunStatus.Succeeded, Grade = new BenchmarkGrade { Passed = false, Detail = "tests-failed-exit-1", EvidenceArtifactId = artifactId }, McpFullCatalog = false, FormatFaultRespawns = _respawns, TokenUsage = usage };
        }
    }
}
