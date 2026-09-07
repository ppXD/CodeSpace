using System.Text.Json;
using CodeSpace.Core.Services.Agents.Eval.Benchmark;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Agents.Benchmark;
using CodeSpace.Messages.Contracts;
using CodeSpace.Messages.Enums;
using Shouldly;

namespace CodeSpace.UnitTests.Agents;

/// <summary>
/// 🟢 Unit: Q2's qualification statistics + grant fold. Pins: the Wilson one-sided lower bound against known
/// values (the number a public claim may cite is the BOUND, never the point estimate); infra-unknown cells sit in
/// the denominator so a broken evaluator can only LOWER it; Sealed requires BOTH the bound and evaluator health
/// to clear — an infra-riddled round or an empty suite mints Shadow evidence, never a sealed claim.
/// </summary>
[Trait("Category", "Unit")]
public class QualificationRunnerTests
{
    [Theory]
    [InlineData(0, 0, 0.0)]        // empty suite ⇒ no claim
    [InlineData(0, 20, 0.0)]       // zero successes ⇒ bound 0
    [InlineData(20, 20, 0.881)]    // perfect 20/20 still bounds WELL below 1 — small n is honest
    [InlineData(15, 20, 0.567)]    // 75% point estimate bounds at ~57%
    [InlineData(150, 200, 0.696)]  // same rate, more n ⇒ tighter bound
    public void The_wilson_lower_bound_matches_known_values(int successes, int trials, double expected)
    {
        QualificationStatistics.WilsonLowerBound(successes, trials).ShouldBe(expected, tolerance: 0.001);
    }

    [Fact]
    public void Infra_unknown_cells_can_only_lower_the_bound()
    {
        var clean = QualificationStatistics.WilsonLowerBound(15, 20);
        var infraRidden = QualificationStatistics.WilsonLowerBound(15, 30);   // 10 infra cells joined the denominator

        infraRidden.ShouldBeLessThan(clean, "a broken evaluator must never inflate capability — its dead cells stay in the divisor");
    }

    [Fact]
    public void Sealed_requires_both_the_bound_and_evaluator_health()
    {
        var spec = new QualificationSpec { MinSolveRateLowerBound = 0.5, MinEvaluatorHealth = 0.9, ValidityDays = 30 };

        QualificationRunner.Grant(spec, Score(solved: 18, unsolved: 2, infra: 0), lowerBound: 0.7, BenchmarkExecutionPath.TaskLaunch)
            .ShouldBe(PerformanceQualification.Sealed);

        QualificationRunner.Grant(spec, Score(solved: 18, unsolved: 2, infra: 0), lowerBound: 0.4, BenchmarkExecutionPath.TaskLaunch)
            .ShouldBe(PerformanceQualification.Shadow, "below the bound bar — measured evidence, no sealed claim");

        QualificationRunner.Grant(spec, Score(solved: 18, unsolved: 0, infra: 4), lowerBound: 0.7, BenchmarkExecutionPath.TaskLaunch)
            .ShouldBe(PerformanceQualification.Shadow, "an infra-riddled round proves nothing about the model — the instrument must be healthy to seal");

        QualificationRunner.Grant(spec, Score(solved: 0, unsolved: 0, infra: 0), lowerBound: 1.0, BenchmarkExecutionPath.TaskLaunch)
            .ShouldBe(PerformanceQualification.Shadow, "an empty suite seals nothing");
    }

    [Fact]
    public void A_direct_agent_harness_cannot_seal_a_product_launch_mode()
    {
        var spec = new QualificationSpec { MinSolveRateLowerBound = 0.5, MinEvaluatorHealth = 0.9, ValidityDays = 30 };

        QualificationRunner.Grant(spec, Score(solved: 20, unsolved: 0, infra: 0), lowerBound: 0.9, BenchmarkExecutionPath.DirectAgentHarness)
            .ShouldBe(PerformanceQualification.Shadow, "a direct agent harness did not exercise TaskLaunch routing, projection, workflow execution, or completion authority");
    }

    [Fact]
    public void BuildCensus_covers_every_manifest_cell_including_a_specified_arm_that_never_ran()
    {
        // P19: "指定 arm 未跑不可被平均遮蔽" — a SPECIFIED arm the corpus loop never reached must still land as its
        // own census row, sourced from the FIXED-denominator Cells, never merely the Results that happened to run.
        var ranResult = new BenchmarkResult
        {
            TaskId = "task-a", Mode = BenchmarkMode.TaskLaunchQuick, RunStatus = AgentRunStatus.Succeeded,
            Grade = new BenchmarkGrade { Passed = true, Detail = "tests-passed" }, McpFullCatalog = false,
            RouteEffortMode = "quick", RouteProjectionKind = "single-agent", ObservedModel = "claude-example",
        };

        var run = new CorpusBenchmarkRun
        {
            ExecutionPath = BenchmarkExecutionPath.TaskLaunch,
            Results = new[] { ranResult },
            Errored = Array.Empty<CorpusBenchmarkError>(),
            Scorecard = new AgentRunScorecard { Harnesses = Array.Empty<HarnessScore>(), Overall = new HarnessScore { Harness = "overall", Total = 0, Succeeded = 0, SuccessRate = 0 } },
            Cells = new[]
            {
                new CorpusCellOutcome { TaskId = "task-a", Mode = BenchmarkMode.TaskLaunchQuick, State = CorpusCellState.Solved },
                new CorpusCellOutcome { TaskId = "task-a", Mode = BenchmarkMode.TaskLaunchDeep, State = CorpusCellState.InfraUnknown, Detail = "the corpus loop never reached this cell" },
            },
        };

        var rows = JsonDocument.Parse(JsonSerializer.Serialize(QualificationRunner.BuildCensus(run))).RootElement.EnumerateArray().ToList();

        rows.Count.ShouldBe(2, "the Deep arm never ran but still gets its own row — never silently absent");

        var ranRow = rows.Single(r => r.GetProperty("arm").GetString() == "TaskLaunchQuick");
        ranRow.GetProperty("state").GetString().ShouldBe("Solved");
        ranRow.GetProperty("routeEffortMode").GetString().ShouldBe("quick");
        ranRow.GetProperty("routeProjectionKind").GetString().ShouldBe("single-agent");
        ranRow.GetProperty("observedModel").GetString().ShouldBe("claude-example");

        var missingRow = rows.Single(r => r.GetProperty("arm").GetString() == "TaskLaunchDeep");
        missingRow.GetProperty("state").GetString().ShouldBe("InfraUnknown", "a specified-but-unrun arm reads as InfraUnknown, the suite's own never-dropped-from-the-divisor cell state");
        missingRow.GetProperty("observedModel").ValueKind.ShouldBe(JsonValueKind.Null, "an arm that never ran has no observed model to report — unknown stays unknown, never backfilled");
    }

    [Fact]
    public void BuildCensus_reports_an_unknown_observed_model_as_null_never_backfilled()
    {
        var result = new BenchmarkResult
        {
            TaskId = "task-a", Mode = BenchmarkMode.TaskLaunchQuick, RunStatus = AgentRunStatus.Succeeded,
            Grade = new BenchmarkGrade { Passed = true, Detail = "tests-passed" }, McpFullCatalog = false,
            RouteEffortMode = "quick", RouteProjectionKind = "single-agent", ObservedModel = null,
        };

        var run = new CorpusBenchmarkRun
        {
            ExecutionPath = BenchmarkExecutionPath.TaskLaunch,
            Results = new[] { result },
            Errored = Array.Empty<CorpusBenchmarkError>(),
            Scorecard = new AgentRunScorecard { Harnesses = Array.Empty<HarnessScore>(), Overall = new HarnessScore { Harness = "overall", Total = 0, Succeeded = 0, SuccessRate = 0 } },
            Cells = new[] { new CorpusCellOutcome { TaskId = "task-a", Mode = BenchmarkMode.TaskLaunchQuick, State = CorpusCellState.Solved } },
        };

        var row = JsonDocument.Parse(JsonSerializer.Serialize(QualificationRunner.BuildCensus(run))).RootElement.EnumerateArray().Single();

        row.GetProperty("observedModel").ValueKind.ShouldBe(JsonValueKind.Null);
    }

    private static CorpusCellScore Score(int solved, int unsolved, int infra) =>
        new() { Solved = solved, Unsolved = unsolved, Abstained = 0, InfraUnknown = infra };
}
