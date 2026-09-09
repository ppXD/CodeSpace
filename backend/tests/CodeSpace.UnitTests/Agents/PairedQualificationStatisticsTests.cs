using CodeSpace.Core.Services.Agents.Eval.Benchmark;
using CodeSpace.Messages.Agents.Benchmark;
using CodeSpace.Messages.Enums;
using Shouldly;

namespace CodeSpace.UnitTests.Agents;

[Trait("Category", "Unit")]
public sealed class PairedQualificationStatisticsTests
{
    [Fact]
    public void A_large_frozen_paired_lift_with_settled_cost_and_distinct_observed_models_qualifies()
    {
        var fixture = Scenario(50, new ScenarioOptions { CandidateSolved = true });

        var outcome = Analyze(fixture, Spec(minimumClusters: 40));

        outcome.QualifiedForCapabilityClaim.ShouldBeTrue();
        outcome.QualityDifference.ShouldBe(1);
        outcome.QualityDifferenceLower95.ShouldBe(1);
        outcome.IndependentClusters.ShouldBe(50);
        outcome.Control.ObservedModels.ShouldBe(new[] { "control-observed" });
        outcome.Candidate.ObservedModels.ShouldBe(new[] { "candidate-observed" });
        outcome.BlockingReasons.ShouldBeEmpty();
    }

    [Fact]
    public void Repeated_sessions_do_not_inflate_the_independent_cluster_count()
    {
        var fixture = Scenario(3, new ScenarioOptions { CandidateSolved = true, Sessions = 5 });

        var outcome = Analyze(fixture, Spec(minimumClusters: 4));

        outcome.PairedCells.ShouldBe(15);
        outcome.IndependentClusters.ShouldBe(3, "five repeats of one task remain one independent source");
        outcome.Strata.Sum(stratum => stratum.IndependentClusters).ShouldBe(3, "stratum disclosure must not relabel repeated sessions as independent evidence");
        outcome.QualifiedForCapabilityClaim.ShouldBeFalse();
        outcome.BlockingReasons.ShouldContain("insufficient-independent-clusters");
    }

    [Fact]
    public void A_large_aggregate_lift_cannot_hide_a_regression_in_one_frozen_stratum()
    {
        var tasks = Enumerable.Range(0, 300).Select(index => Task($"task-{index}") with
        {
            Stratum = index < 40 ? "safety-critical" : "general", IndependenceCluster = $"source-{index}",
        }).ToList();
        var suite = new HiddenSuite(tasks, "sha256:hidden", new NoopStager());
        var fixture = new Fixture(tasks, suite, EvalSuite.ManifestFor(tasks, suite.SuiteContentHash), new List<PairedCorpusBenchmarkRun>
        {
            new()
            {
                Control = RunSelective(tasks, index => index < 40, "control-observed"),
                Candidate = RunSelective(tasks, index => index is >= 40 and < 240, "candidate-observed"),
            },
        });

        var outcome = Analyze(fixture, Spec(minimumClusters: 300) with { MinimumStrata = 2 });

        outcome.QualityDifference.ShouldBeGreaterThan(0.5);
        outcome.QualityDifferenceLower95.ShouldBeGreaterThan(0);
        outcome.QualifiedForCapabilityClaim.ShouldBeFalse("a generic claim cannot trade away one frozen task family behind a strong aggregate");
        outcome.BlockingReasons.ShouldContain("stratum-regression");
        var regressed = outcome.Strata.Single(stratum => stratum.Stratum == "safety-critical");
        regressed.Pairs.ShouldBe(40);
        regressed.IndependentClusters.ShouldBe(40);
        regressed.QualityDifference.ShouldBe(-1);
        regressed.QualityDifferenceLower95.ShouldBe(-1);
    }

    [Fact]
    public void Required_infra_unknown_and_unknown_solved_cost_remain_visible_and_block()
    {
        var fixture = Scenario(40, new ScenarioOptions { CandidateSolved = true, Required = true });
        var first = fixture.Sessions[0];
        fixture.Sessions[0] = first with
        {
            Candidate = Run(fixture.Tasks, true, "candidate-observed", new RunOptions { Cost = 0.5m, InfraTask = "task-0", IndeterminateTask = "task-1" }),
        };

        var outcome = Analyze(fixture, Spec(minimumClusters: 40) with { MinimumRequiredExecutionClusters = 1 });

        outcome.RequiredExecutionComplete.ShouldBeFalse();
        outcome.Candidate.InfraUnknown.ShouldBe(1);
        outcome.InfraFailures.ShouldHaveSingleItem().TaskId.ShouldBe("task-0");
        outcome.Candidate.Solved.ShouldBe(39);
        outcome.Candidate.BudgetAdmissibleSolved.ShouldBe(38, "the solved cell with unknown spend stays in the denominator but cannot enter the budget-admissible numerator");
        outcome.BlockingReasons.ShouldContain("required-execution-incomplete");
    }

    [Fact]
    public void Two_configured_rows_that_resolve_to_one_backing_model_are_not_an_independent_comparison()
    {
        var fixture = Scenario(40, new ScenarioOptions { CandidateSolved = true, CandidateObserved = "CONTROL-OBSERVED" });

        var outcome = Analyze(fixture, Spec(minimumClusters: 40));

        outcome.QualifiedForCapabilityClaim.ShouldBeFalse();
        outcome.BlockingReasons.ShouldContain("identical-observed-model");
    }

    [Fact]
    public void A_capability_cell_without_observed_model_identity_blocks_the_claim()
    {
        var fixture = Scenario(40, new ScenarioOptions { CandidateSolved = true });
        var first = fixture.Sessions[0];
        fixture.Sessions[0] = first with { Candidate = Run(fixture.Tasks, true, "candidate-observed", new RunOptions { WithoutObservedTask = "task-0" }) };

        var outcome = Analyze(fixture, Spec(minimumClusters: 40));

        outcome.Candidate.CapabilityVerdictCells.ShouldBe(40);
        outcome.Candidate.ObservedModelCells.ShouldBe(39);
        outcome.BlockingReasons.ShouldContain("incomplete-or-inconsistent-observed-model");
        outcome.QualifiedForCapabilityClaim.ShouldBeFalse();
    }

    [Fact]
    public void Missing_sessions_or_non_TaskLaunch_evidence_cannot_be_claimed_as_the_frozen_protocol()
    {
        var fixture = Scenario(40, new ScenarioOptions { CandidateSolved = true });
        fixture.Sessions[0] = fixture.Sessions[0] with { Candidate = fixture.Sessions[0].Candidate with { ExecutionPath = BenchmarkExecutionPath.DirectAgentHarness } };

        var outcome = Analyze(fixture, Spec(minimumClusters: 40) with { SessionsPerCell = 2 });

        outcome.BlockingReasons.ShouldContain("paired-protocol-evidence-mismatch");
        outcome.QualifiedForCapabilityClaim.ShouldBeFalse();
    }

    [Fact]
    public void Efficiency_requires_quality_noninferiority_and_complete_costs_per_admissible_solve()
    {
        var fixture = Scenario(40, new ScenarioOptions { ControlSolved = true, CandidateSolved = true, CandidateCost = 0.5m });
        var spec = Spec(minimumClusters: 40) with { Criterion = PairedQualificationCriterion.Efficiency, MinimumCostReduction = 0.2 };

        var outcome = Analyze(fixture, spec);

        outcome.QualityDifference.ShouldBe(0);
        outcome.QualityDifferenceLower95.ShouldBe(0);
        outcome.CostReduction.ShouldNotBeNull().ShouldBe(0.5, tolerance: 0.0001);
        outcome.QualifiedForCapabilityClaim.ShouldBeTrue();
    }

    [Fact]
    public void Free_form_strata_and_cluster_metadata_change_the_frozen_protocol_identity()
    {
        var task = Task("a") with { Stratum = "repository-migration", IndependenceCluster = "source-1", RequiresCompleteExecution = true };
        var digest = EvalSuite.ManifestFor(new[] { task }).Version;

        EvalSuite.ManifestFor(new[] { task with { Stratum = "research" } }).Version.ShouldNotBe(digest);
        EvalSuite.ManifestFor(new[] { task with { IndependenceCluster = "source-2" } }).Version.ShouldNotBe(digest);
        EvalSuite.ManifestFor(new[] { task with { RequiresCompleteExecution = false } }).Version.ShouldNotBe(digest);
    }

    [Fact]
    public void Invalid_revision_provenance_cannot_produce_a_capability_claim()
    {
        var fixture = Scenario(50, new ScenarioOptions { CandidateSolved = true });

        var outcome = PairedQualificationStatistics.Analyze(new PairedQualificationAnalysisRequest
        {
            ObservationGroupId = Guid.NewGuid(), CodeRevision = "main", Suite = fixture.Suite, Manifest = fixture.Manifest,
            Spec = Spec(minimumClusters: 40), Control = Selection("control"), Candidate = Selection("candidate"), Sessions = fixture.Sessions,
        });

        outcome.QualifiedForCapabilityClaim.ShouldBeFalse();
        outcome.BlockingReasons.ShouldContain("code-revision-invalid");
    }

    private static PairedQualificationOutcome Analyze(Fixture fixture, PairedQualificationSpec spec) =>
        PairedQualificationStatistics.Analyze(new PairedQualificationAnalysisRequest
        {
            ObservationGroupId = Guid.NewGuid(), CodeRevision = new string('a', 40), Suite = fixture.Suite, Manifest = fixture.Manifest,
            Spec = spec, Control = Selection("control"), Candidate = Selection("candidate"), Sessions = fixture.Sessions,
        });

    private static Fixture Scenario(int tasks, ScenarioOptions? options = null)
    {
        options ??= new ScenarioOptions();
        var definitions = Enumerable.Range(0, tasks).Select(index => Task($"task-{index}") with
        {
            Stratum = index % 2 == 0 ? "even" : "odd", IndependenceCluster = $"source-{index}", RequiresCompleteExecution = options.Required,
        }).ToList();
        var suite = new HiddenSuite(definitions, "sha256:hidden", new NoopStager());
        var runs = Enumerable.Range(0, options.Sessions).Select(_ => new PairedCorpusBenchmarkRun
        {
            Control = Run(definitions, options.ControlSolved, "control-observed", new RunOptions { Cost = options.ControlCost }),
            Candidate = Run(definitions, options.CandidateSolved, options.CandidateObserved, new RunOptions { Cost = options.CandidateCost }),
        }).ToList();
        return new Fixture(definitions, suite, EvalSuite.ManifestFor(definitions, suite.SuiteContentHash), runs);
    }

    private static CorpusBenchmarkRun Run(IReadOnlyList<BenchmarkTask> tasks, bool solved, string observed, RunOptions? options = null)
    {
        options ??= new RunOptions();
        var cells = tasks.Select(task => new CorpusCellOutcome
        {
            TaskId = task.Id, Mode = BenchmarkMode.TaskLaunchQuick,
            State = task.Id == options.InfraTask ? CorpusCellState.InfraUnknown : solved ? CorpusCellState.Solved : CorpusCellState.Unsolved,
            Detail = task.Id == options.InfraTask ? "gateway unavailable" : null,
        }).ToList();
        var results = tasks.Where(task => task.Id != options.InfraTask).Select(task => new BenchmarkResult
        {
            TaskId = task.Id, Mode = BenchmarkMode.TaskLaunchQuick, RunStatus = AgentRunStatus.Succeeded,
            Grade = new BenchmarkGrade { Passed = solved, Detail = solved ? "passed" : "failed" }, McpFullCatalog = false,
            ObservedModel = task.Id == options.WithoutObservedTask ? null : observed, CostUsd = task.Id == options.IndeterminateTask ? null : options.Cost,
            CostIndeterminate = task.Id == options.IndeterminateTask,
        }).ToList();
        return new CorpusBenchmarkRun
        {
            ExecutionPath = BenchmarkExecutionPath.TaskLaunch, Results = results,
            Errored = options.InfraTask is null ? Array.Empty<CorpusBenchmarkError>() : new[] { new CorpusBenchmarkError { TaskId = options.InfraTask, Mode = BenchmarkMode.TaskLaunchQuick, Error = "gateway unavailable" } },
            Scorecard = BenchmarkScorecard.Compute(results), SuiteVersion = EvalSuite.ManifestFor(tasks, "sha256:hidden").Version, Cells = cells,
        };
    }

    private static CorpusBenchmarkRun RunSelective(IReadOnlyList<BenchmarkTask> tasks, Func<int, bool> solved, string observed)
    {
        var results = tasks.Select((task, index) => new BenchmarkResult
        {
            TaskId = task.Id, Mode = BenchmarkMode.TaskLaunchQuick, RunStatus = AgentRunStatus.Succeeded,
            Grade = new BenchmarkGrade { Passed = solved(index), Detail = solved(index) ? "passed" : "failed" }, McpFullCatalog = false,
            ObservedModel = observed, CostUsd = 1m,
        }).ToList();
        var cells = tasks.Select((task, index) => new CorpusCellOutcome
        {
            TaskId = task.Id, Mode = BenchmarkMode.TaskLaunchQuick, State = solved(index) ? CorpusCellState.Solved : CorpusCellState.Unsolved,
        }).ToList();
        return new CorpusBenchmarkRun
        {
            ExecutionPath = BenchmarkExecutionPath.TaskLaunch, Results = results, Errored = Array.Empty<CorpusBenchmarkError>(),
            Scorecard = BenchmarkScorecard.Compute(results), SuiteVersion = EvalSuite.ManifestFor(tasks, "sha256:hidden").Version, Cells = cells,
        };
    }

    private static BenchmarkTask Task(string id) => new()
    {
        Id = id, Description = id, Goal = "solve", FixtureRef = id, Harness = "claude-code",
        Grading = BenchmarkGradingKind.TestsPass, TestCommand = new[] { "sh", "check.sh" }, Modes = new[] { BenchmarkMode.TaskLaunchQuick },
    };

    private static BenchmarkAgentSelection Selection(string model) => new()
    {
        Harness = "claude-code", Model = model, ModelCredentialId = Guid.NewGuid(), ModelCredentialModelId = Guid.NewGuid(),
    };

    private static PairedQualificationSpec Spec(int minimumClusters) => new()
    {
        SessionsPerCell = 1, MinimumIndependentClusters = minimumClusters, MinimumStrata = 1, MinimumRequiredExecutionClusters = 0, MinimumEvaluatorHealth = 0.9,
        MaxCostUsdPerLaunch = 5m, Criterion = PairedQualificationCriterion.Quality, OrderingSeed = "pre-registered-seed",
    };

    private sealed record Fixture(IReadOnlyList<BenchmarkTask> Tasks, HiddenSuite Suite, EvalSuiteManifest Manifest, List<PairedCorpusBenchmarkRun> Sessions);
    private sealed record ScenarioOptions
    {
        public bool ControlSolved { get; init; }
        public bool CandidateSolved { get; init; }
        public int Sessions { get; init; } = 1;
        public bool Required { get; init; }
        public string CandidateObserved { get; init; } = "candidate-observed";
        public decimal ControlCost { get; init; } = 1m;
        public decimal CandidateCost { get; init; } = 1m;
    }
    private sealed record RunOptions
    {
        public decimal Cost { get; init; } = 1m;
        public string? InfraTask { get; init; }
        public string? IndeterminateTask { get; init; }
        public string? WithoutObservedTask { get; init; }
    }
    private sealed class NoopStager : IBenchmarkFixtureStager { public void Stage(string fixtureRef, string directory) { } }
}
