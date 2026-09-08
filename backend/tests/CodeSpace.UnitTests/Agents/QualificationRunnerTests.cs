using System.Text.Json;
using CodeSpace.Core.Services.Agents.Eval.Benchmark;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Agents.Benchmark;
using CodeSpace.Messages.Contracts;
using CodeSpace.Messages.Dtos.Workflows;
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
            CompletionMode = WorkflowDefinition.CompletionModeShadow,
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
        ranRow.GetProperty("completionMode").GetString().ShouldBe("shadow", "a TaskLaunch cell always forces Shadow — the census must show it, never omit it");

        var missingRow = rows.Single(r => r.GetProperty("arm").GetString() == "TaskLaunchDeep");
        missingRow.GetProperty("state").GetString().ShouldBe("InfraUnknown", "a specified-but-unrun arm reads as InfraUnknown, the suite's own never-dropped-from-the-divisor cell state");
        missingRow.GetProperty("observedModel").ValueKind.ShouldBe(JsonValueKind.Null, "an arm that never ran has no observed model to report — unknown stays unknown, never backfilled");
        missingRow.GetProperty("completionMode").ValueKind.ShouldBe(JsonValueKind.Null, "an arm that never ran has no run to read a completion mode off");
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

    [Fact]
    public void Complete_single_identity_TaskLaunch_evidence_binds_to_the_selected_model_row()
    {
        var rowId = Guid.NewGuid();
        var run = Run(BenchmarkExecutionPath.TaskLaunch,
            Result("a", "gateway-model-v2"), Result("b", " GATEWAY-MODEL-V2 "),
            Cell("a", CorpusCellState.Solved), Cell("b", CorpusCellState.Unsolved));

        var evidence = QualificationRunner.BuildModelEvidence(new BenchmarkAgentSelection { ModelCredentialModelId = rowId, Model = "configured-alias" }, run, Score(1, 1, 0), 0.17);

        evidence.Attribution.ShouldBe(ModelQualificationAttribution.Bound);
        evidence.ModelCredentialModelId.ShouldBe(rowId);
        evidence.RequestedModel.ShouldBe("configured-alias");
        evidence.ObservedModel.ShouldBe("gateway-model-v2");
        evidence.ObservedCellCount.ShouldBe(2);
        evidence.SampleSize.ShouldBe(2);
    }

    [Fact]
    public void Missing_or_inconsistent_observed_identity_never_gets_bound_to_a_model_row()
    {
        var selection = new BenchmarkAgentSelection { ModelCredentialModelId = Guid.NewGuid(), Model = "configured-alias" };
        var missing = Run(BenchmarkExecutionPath.TaskLaunch,
            Result("a", "gateway-model"), Result("b", null),
            Cell("a", CorpusCellState.Solved), Cell("b", CorpusCellState.Unsolved));
        var inconsistent = Run(BenchmarkExecutionPath.TaskLaunch,
            Result("a", "gateway-model-a"), Result("b", "gateway-model-b"),
            Cell("a", CorpusCellState.Solved), Cell("b", CorpusCellState.Unsolved));

        QualificationRunner.BuildModelEvidence(selection, missing, Score(1, 1, 0), 0.17).Attribution.ShouldBe(ModelQualificationAttribution.MissingObservation);
        QualificationRunner.BuildModelEvidence(selection, inconsistent, Score(1, 1, 0), 0.17).Attribution.ShouldBe(ModelQualificationAttribution.InconsistentObservation);
    }

    [Fact]
    public void Direct_harness_or_unbound_selection_is_measured_but_never_model_attributed()
    {
        var taskLaunch = Run(BenchmarkExecutionPath.TaskLaunch, Result("a", "model"), Cell("a", CorpusCellState.Solved));
        var direct = taskLaunch with { ExecutionPath = BenchmarkExecutionPath.DirectAgentHarness };

        QualificationRunner.BuildModelEvidence(new BenchmarkAgentSelection(), taskLaunch, Score(1, 0, 0), 0.4).Attribution.ShouldBe(ModelQualificationAttribution.UnboundSelection);
        QualificationRunner.BuildModelEvidence(new BenchmarkAgentSelection { ModelCredentialModelId = Guid.NewGuid() }, direct, Score(1, 0, 0), 0.4).Attribution.ShouldBe(ModelQualificationAttribution.NonLaunchPath);
    }

    private static CorpusBenchmarkRun Run(BenchmarkExecutionPath path, params object[] rows) => new()
    {
        ExecutionPath = path,
        Results = rows.OfType<BenchmarkResult>().ToList(),
        Cells = rows.OfType<CorpusCellOutcome>().ToList(),
        Errored = Array.Empty<CorpusBenchmarkError>(),
        Scorecard = new AgentRunScorecard { Harnesses = Array.Empty<HarnessScore>(), Overall = new HarnessScore { Harness = "overall", Total = 0, Succeeded = 0, SuccessRate = 0 } },
    };

    private static BenchmarkResult Result(string taskId, string? observedModel) => new()
    {
        TaskId = taskId, Mode = BenchmarkMode.TaskLaunchQuick, RunStatus = AgentRunStatus.Succeeded,
        Grade = new BenchmarkGrade { Passed = true, Detail = "measured" }, McpFullCatalog = false, ObservedModel = observedModel,
    };

    private static CorpusCellOutcome Cell(string taskId, CorpusCellState state) => new() { TaskId = taskId, Mode = BenchmarkMode.TaskLaunchQuick, State = state };

    private static CorpusCellScore Score(int solved, int unsolved, int infra) =>
        new() { Solved = solved, Unsolved = unsolved, Abstained = 0, InfraUnknown = infra };
}
