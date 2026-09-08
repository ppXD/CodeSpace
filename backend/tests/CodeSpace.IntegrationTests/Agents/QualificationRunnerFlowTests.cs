using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Services.Agents.Eval.Benchmark;
using CodeSpace.Core.Services.Completion;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.IntegrationTests.Workflows.Infrastructure;
using CodeSpace.Messages.Agents.Benchmark;
using CodeSpace.Messages.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using System.Text.Json;

namespace CodeSpace.IntegrationTests.Agents;

/// <summary>
/// 🟡 Medium-mock (real Postgres + real receipt store + real statistics; faked suite source + corpus runner —
/// the corpus machinery has its own coverage): Q2's qualification runner end to end — the hidden suite's digest,
/// the frozen-denominator score, the one-sided lower bound, and the granted tier all land on ONE immutable
/// receipt row; an infra-riddled round mints Shadow evidence, never a sealed claim; an absent suite throws
/// (misconfiguration, never a silent pass).
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public class QualificationRunnerFlowTests
{
    private readonly PostgresFixture _fixture;

    public QualificationRunnerFlowTests(PostgresFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task A_healthy_round_above_the_bar_mints_a_sealed_receipt()
    {
        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var mode = "supervisor-" + Guid.NewGuid().ToString("N")[..6];

        using var scope = _fixture.BeginScope();
        var runner = Runner(scope, cells: Cells(solved: 19, unsolved: 1, infra: 0), BenchmarkExecutionPath.TaskLaunch);

        var outcome = await runner.QualifyAsync(mode, "git-branch", Spec(minLowerBound: 0.7), teamId, Selection(), CancellationToken.None);

        outcome.Granted.ShouldBe(PerformanceQualification.Sealed, $"19/20 bounds at ~{outcome.SolveRateLowerBound:F3} — above the 0.7 bar");
        outcome.SuiteDigest.ShouldBe("sha256:fake-suite");

        var row = await scope.Resolve<CodeSpaceDbContext>().QualificationReceipt.AsNoTracking().SingleAsync(r => r.Id == outcome.ReceiptId);
        row.GrantedPerformance.ShouldBe(PerformanceQualification.Sealed);
        row.SuiteDigest.ShouldBe("sha256:fake-suite", "the digest pins WHICH tasks were run — a later suite edit can never claim this number");

        var metrics = JsonDocument.Parse(row.MetricsJson!).RootElement;
        metrics.GetProperty("solved").GetInt32().ShouldBe(19);
        metrics.GetProperty("solveRateLowerBound").GetDouble().ShouldBe(outcome.SolveRateLowerBound);
        metrics.GetProperty("executionPath").GetString().ShouldBe("TaskLaunch");

        // Q5: the round's identity lands as the TYPED nouns — a claim reader parses them back verbatim.
        var cohort = JsonDocument.Parse(row.CohortJson!).RootElement;
        cohort.GetProperty("teamId").GetGuid().ShouldBe(teamId);
        cohort.GetProperty("mode").GetString().ShouldBe(mode);
        cohort.GetProperty("tier").GetString().ShouldBe("internal-qualification");
        cohort.GetProperty("completionPolicyVersion").GetInt32().ShouldBe(CodeSpace.Core.Services.Completion.CompletionPolicy.CurrentVersion);
        var verifier = JsonDocument.Parse(row.VerifierBundleJson!).RootElement;
        verifier.GetProperty("harness").GetString().ShouldBe("codex-cli");
        verifier.GetProperty("executionPath").GetString().ShouldBe("TaskLaunch");
    }

    [Fact]
    public async Task A_direct_harness_round_remains_shadow_even_when_every_oracle_passes()
    {
        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var mode = "supervisor-" + Guid.NewGuid().ToString("N")[..6];

        using var scope = _fixture.BeginScope();
        var runner = Runner(scope, cells: Cells(solved: 20, unsolved: 0, infra: 0), BenchmarkExecutionPath.DirectAgentHarness);

        var outcome = await runner.QualifyAsync(mode, "git-branch", Spec(minLowerBound: 0.5), teamId, Selection(), CancellationToken.None);

        outcome.Granted.ShouldBe(PerformanceQualification.Shadow);
        var row = await scope.Resolve<CodeSpaceDbContext>().QualificationReceipt.AsNoTracking().SingleAsync(r => r.Id == outcome.ReceiptId);
        JsonDocument.Parse(row.MetricsJson!).RootElement.GetProperty("executionPath").GetString().ShouldBe("DirectAgentHarness");
    }

    [Fact]
    public async Task An_infra_riddled_round_mints_shadow_evidence_never_a_seal()
    {
        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var mode = "supervisor-" + Guid.NewGuid().ToString("N")[..6];

        using var scope = _fixture.BeginScope();
        var runner = Runner(scope, cells: Cells(solved: 19, unsolved: 0, infra: 5));

        var outcome = await runner.QualifyAsync(mode, "git-branch", Spec(minLowerBound: 0.5), teamId, Selection(), CancellationToken.None);

        outcome.Granted.ShouldBe(PerformanceQualification.Shadow, "a broken evaluator proves nothing about the model — the round records evidence, no sealed claim");
        (await scope.Resolve<IQualificationReceiptStore>().ListCurrentAsync(mode, "git-branch", DateTimeOffset.UtcNow, CancellationToken.None))
            .ShouldHaveSingleItem().GrantedPerformance.ShouldBe(PerformanceQualification.Shadow);
    }

    [Fact]
    public async Task A_specified_arm_that_never_ran_lands_in_the_census_as_unmeasured_and_the_rate_still_counts_it()
    {
        // P19: "指定 arm 未跑不可被平均遮蔽" — an arm the corpus never reached must still be a CENSUS ROW (state
        // InfraUnknown), and the minted solve rate must still be computed over the FULL denominator (including it),
        // never silently over just the arms that happened to run.
        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var mode = "supervisor-" + Guid.NewGuid().ToString("N")[..6];

        var ranResult = new BenchmarkResult
        {
            TaskId = "t1", Mode = BenchmarkMode.TaskLaunchQuick, RunStatus = CodeSpace.Messages.Enums.AgentRunStatus.Succeeded,
            Grade = new BenchmarkGrade { Passed = true, Detail = "tests-passed" }, McpFullCatalog = false,
            RouteEffortMode = "quick", RouteProjectionKind = "single-agent", ObservedModel = "claude-example",
        };
        var cells = new[]
        {
            new CorpusCellOutcome { TaskId = "t1", Mode = BenchmarkMode.TaskLaunchQuick, State = CorpusCellState.Solved },
            new CorpusCellOutcome { TaskId = "t1", Mode = BenchmarkMode.TaskLaunchDeep, State = CorpusCellState.InfraUnknown, Detail = "the corpus loop never reached this cell" },
        };

        using var scope = _fixture.BeginScope();
        var runner = Runner(scope, cells, BenchmarkExecutionPath.TaskLaunch, results: new[] { ranResult });

        // The Deep arm never ran, so a 0.9 bar over just the ONE ran cell would seal; the specified-but-missing
        // arm must instead pull the round back to Shadow by widening the denominator to 2.
        var outcome = await runner.QualifyAsync(mode, "git-branch", Spec(minLowerBound: 0.9), teamId, Selection(), CancellationToken.None);

        outcome.Score.Solved.ShouldBe(1);
        outcome.Score.InfraUnknown.ShouldBe(1, "the missing Deep arm still occupies its cell — never dropped from the divisor");
        outcome.Score.Total.ShouldBe(2, "the denominator is BOTH arms, not just the one that ran");
        outcome.Granted.ShouldBe(PerformanceQualification.Shadow, "a specified arm that never ran must be able to pull a round back from Sealed — never averaged away");

        var row = await scope.Resolve<CodeSpaceDbContext>().QualificationReceipt.AsNoTracking().SingleAsync(r => r.Id == outcome.ReceiptId);
        var census = JsonDocument.Parse(row.MetricsJson!).RootElement.GetProperty("census").EnumerateArray().ToList();

        census.Count.ShouldBe(2, "both the ran arm and the never-run arm get their own census row");

        var ranRow = census.Single(r => r.GetProperty("arm").GetString() == "TaskLaunchQuick");
        ranRow.GetProperty("state").GetString().ShouldBe("Solved");
        ranRow.GetProperty("routeEffortMode").GetString().ShouldBe("quick");
        ranRow.GetProperty("routeProjectionKind").GetString().ShouldBe("single-agent");
        ranRow.GetProperty("observedModel").GetString().ShouldBe("claude-example");

        var missingRow = census.Single(r => r.GetProperty("arm").GetString() == "TaskLaunchDeep");
        missingRow.GetProperty("state").GetString().ShouldBe("InfraUnknown", "a specified-but-unrun arm is UNMEASURED — it must never vanish from the census");
        missingRow.GetProperty("observedModel").ValueKind.ShouldBe(JsonValueKind.Null);
    }

    [Fact]
    public async Task An_unknown_fixture_is_an_explicit_infra_fault_for_a_TaskLaunch_arm_never_a_silent_skip()
    {
        // P19: the real fixture stager throws for an unknown FixtureRef — the SAME production stager the direct
        // instrument uses. A Launch-mode task must get exactly the same explicit-infra-fault treatment.
        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var mode = "supervisor-" + Guid.NewGuid().ToString("N")[..6];
        var unknownTask = Task_() with { FixtureRef = "does-not-exist-" + Guid.NewGuid().ToString("N"), Modes = new[] { BenchmarkMode.TaskLaunchQuick } };

        using var scope = _fixture.BeginScope();
        var corpus = scope.Resolve<ICorpusBenchmarkRunner>();   // the REAL corpus loop + REAL production SeedFixtureStager

        var run = await corpus.RunAsync(new[] { unknownTask }, teamId, selection: null, CancellationToken.None);

        run.Errored.ShouldHaveSingleItem().TaskId.ShouldBe(unknownTask.Id);
        run.Cells!.ShouldHaveSingleItem().State.ShouldBe(CorpusCellState.InfraUnknown, "an unknown fixture is an explicit infra fault, occupying its cell — never a silent skip");
        run.Results.ShouldBeEmpty();
    }

    [Fact]
    public async Task An_absent_suite_throws_never_a_silent_pass()
    {
        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);

        using var scope = _fixture.BeginScope();
        var runner = new QualificationRunner(new FakeSuiteSource(null), new FakeCorpusRunner(Array.Empty<CorpusCellOutcome>()),
            scope.Resolve<IQualificationReceiptStore>(), NullLogger<QualificationRunner>.Instance);

        var ex = await Should.ThrowAsync<InvalidOperationException>(() =>
            runner.QualifyAsync("supervisor", "git-branch", Spec(0.5), teamId, Selection(), CancellationToken.None));

        ex.Message.ShouldContain("misconfiguration");
    }

    [Fact]
    public async Task The_minting_entry_surfaces_an_absent_suite_as_a_misconfiguration()
    {
        // Q-ops: the global-admin command → handler → runner wiring, driven end-to-end. On a host with no staged
        // suite (this CI runner), the round must THROW naming the misconfiguration — never mint, never silently pass.
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);

        using var scope = _fixture.BeginScopeAs(userId, teamId, CodeSpace.Messages.Constants.Roles.Admin);
        var ex = await Should.ThrowAsync<InvalidOperationException>(() => scope.Resolve<MediatR.IMediator>().Send(new CodeSpace.Messages.Commands.Agents.RunQualificationRoundCommand
        {
            Mode = "supervisor", CapabilityKey = "git-branch", MinSolveRateLowerBound = 0.5,
        }, CancellationToken.None));

        ex.Message.ShouldContain("misconfiguration");
    }

    // ─── Plumbing ────────────────────────────────────────────────────────────────

    private static QualificationRunner Runner(ILifetimeScope scope, IReadOnlyList<CorpusCellOutcome> cells, BenchmarkExecutionPath executionPath = BenchmarkExecutionPath.DirectAgentHarness, IReadOnlyList<BenchmarkResult>? results = null) =>
        new(new FakeSuiteSource(new HiddenSuite(new[] { Task_() }, "sha256:fake-suite", new CodeSpace.Core.Services.Agents.Eval.Benchmark.Stagers.SeedFixtureStager())), new FakeCorpusRunner(cells, executionPath, results),
            scope.Resolve<IQualificationReceiptStore>(), NullLogger<QualificationRunner>.Instance);

    private static QualificationSpec Spec(double minLowerBound) => new() { MinSolveRateLowerBound = minLowerBound, MinEvaluatorHealth = 0.9, ValidityDays = 30 };

    private static BenchmarkAgentSelection Selection() => new() { Harness = "codex-cli", Model = "test-model" };

    private static BenchmarkTask Task_() => new() { Id = "t1", Description = "d", Goal = "g", FixtureRef = "f1", Harness = "codex-cli", Modes = new[] { BenchmarkMode.HarnessCli }, Grading = BenchmarkGradingKind.TestsPass, TestCommand = new[] { "sh", "check.sh" } };

    private static IReadOnlyList<CorpusCellOutcome> Cells(int solved, int unsolved, int infra)
    {
        var cells = new List<CorpusCellOutcome>();
        for (var i = 0; i < solved; i++) cells.Add(new CorpusCellOutcome { TaskId = $"s{i}", Mode = BenchmarkMode.HarnessCli, State = CorpusCellState.Solved });
        for (var i = 0; i < unsolved; i++) cells.Add(new CorpusCellOutcome { TaskId = $"u{i}", Mode = BenchmarkMode.HarnessCli, State = CorpusCellState.Unsolved });
        for (var i = 0; i < infra; i++) cells.Add(new CorpusCellOutcome { TaskId = $"i{i}", Mode = BenchmarkMode.HarnessCli, State = CorpusCellState.InfraUnknown });
        return cells;
    }

    private sealed class FakeSuiteSource : IHiddenSuiteSource
    {
        private readonly HiddenSuite? _suite;
        public FakeSuiteSource(HiddenSuite? suite) => _suite = suite;
        public HiddenSuite? Load() => _suite;
    }

    private sealed class FakeCorpusRunner : ICorpusBenchmarkRunner
    {
        private readonly IReadOnlyList<CorpusCellOutcome> _cells;
        private readonly BenchmarkExecutionPath _executionPath;
        private readonly IReadOnlyList<BenchmarkResult> _results;
        public FakeCorpusRunner(IReadOnlyList<CorpusCellOutcome> cells, BenchmarkExecutionPath executionPath = BenchmarkExecutionPath.DirectAgentHarness, IReadOnlyList<BenchmarkResult>? results = null)
        {
            _cells = cells; _executionPath = executionPath; _results = results ?? Array.Empty<BenchmarkResult>();
        }

        public Task<CorpusBenchmarkRun> RunAsync(CorpusBenchmarkRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(new CorpusBenchmarkRun
            {
                Results = _results,
                Errored = Array.Empty<CorpusBenchmarkError>(),
                Scorecard = new CodeSpace.Messages.Agents.AgentRunScorecard { Harnesses = Array.Empty<CodeSpace.Messages.Agents.HarnessScore>(), Overall = new CodeSpace.Messages.Agents.HarnessScore { Harness = "overall", Total = 0, Succeeded = 0, SuccessRate = 0 } },
                Cells = _cells,
                ExecutionPath = _executionPath,
            });
    }
}
