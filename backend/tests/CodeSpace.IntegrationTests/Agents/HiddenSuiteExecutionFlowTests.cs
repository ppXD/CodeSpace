using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Agents.Eval.Benchmark;
using CodeSpace.Core.Services.Completion;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.IntegrationTests.Workflows.Infrastructure;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Agents.Benchmark;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using System.Text.Json;

namespace CodeSpace.IntegrationTests.Agents;

[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public sealed class HiddenSuiteExecutionFlowTests
{
    private readonly PostgresFixture _fixture;

    public HiddenSuiteExecutionFlowTests(PostgresFixture fixture) => _fixture = fixture;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Qualification_executes_private_bytes_and_restages_the_same_source_after_a_gateway_fault(bool retry)
    {
        if (OperatingSystem.IsWindows()) return;
        var directory = Path.Combine(Path.GetTempPath(), "cs-hidden-flow-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(directory, "fixtures/private/nested"));
        try
        {
            var task = new BenchmarkTask
            {
                Id = "private-fixture", Description = "fixture binding plumbing", Goal = "write the answer",
                FixtureRef = "private/nested", Harness = "codex-cli", Grading = BenchmarkGradingKind.TestsPass,
                TestCommand = new[] { "sh", "-c", "test \"$(cat answer.txt 2>/dev/null)\" = correct" }, Modes = new[] { BenchmarkMode.HarnessCli },
            };
            File.WriteAllText(Path.Combine(directory, "tasks.json"), JsonSerializer.Serialize(new[] { task }, AgentJson.Options));
            File.WriteAllText(Path.Combine(directory, "fixtures/private/nested/source.txt"), "frozen");
            var script = retry
                ? "if [ \"$MAX_THINKING_TOKENS\" != \"0\" ]; then\nprintf polluted > source.txt\nprintf wrong > answer.txt\necho 'API Error: Content block is not a thinking block' >&2\nexit 1\nfi\n" +
                    "test \"$(cat source.txt)\" = frozen || exit 2\ntest ! -e answer.txt || exit 3\nprintf correct > answer.txt\n" + FakeBenchmarkCli.NoOpScript
                : FakeBenchmarkCli.NoOpScript;
            using var cli = new FakeBenchmarkCli(script);
            var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
            using var scope = _fixture.BeginScopeAs(userId, teamId);
            var suite = HiddenSuiteLoader.Load(directory);
            var runner = new QualificationRunner(new SuiteSource(suite), scope.Resolve<ICorpusBenchmarkRunner>(), scope.Resolve<IQualificationReceiptStore>(), NullLogger<QualificationRunner>.Instance);

            var outcome = await runner.QualifyAsync("fixture-source-plumbing", "fixture-binding", new QualificationSpec { MinEvaluatorHealth = 1, MinSolveRateLowerBound = 0.99, ValidityDays = 1 }, teamId, new BenchmarkAgentSelection { Harness = "codex-cli", Autonomy = AgentAutonomyLevel.Trusted }, CancellationToken.None);

            outcome.Score.Total.ShouldBe(1);
            outcome.Score.InfraUnknown.ShouldBe(0, "the private reference must never be sent to the seed registry");
            outcome.Score.Solved.ShouldBe(retry ? 1 : 0);
            outcome.Score.Unsolved.ShouldBe(retry ? 0 : 1);
            outcome.FormatFaults.Respawns.ShouldBe(retry ? 1 : 0);
            var receipt = await scope.Resolve<CodeSpaceDbContext>().QualificationReceipt.AsNoTracking().SingleAsync(r => r.Id == outcome.ReceiptId);
            receipt.SuiteDigest.ShouldBe(suite.SuiteContentHash);
            (await scope.Resolve<CodeSpaceDbContext>().AgentRun.AsNoTracking().CountAsync(r => r.TeamId == teamId)).ShouldBe(retry ? 2 : 1);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private sealed record SuiteSource(HiddenSuite Suite) : IHiddenSuiteSource
    {
        public HiddenSuite Load() => Suite;
    }
}
