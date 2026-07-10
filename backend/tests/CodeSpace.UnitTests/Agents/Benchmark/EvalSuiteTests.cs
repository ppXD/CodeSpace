using CodeSpace.Core.Services.Agents.Eval.Benchmark;
using CodeSpace.Messages.Agents.Benchmark;
using Shouldly;

namespace CodeSpace.UnitTests.Agents.Benchmark;

/// <summary>
/// 🟢 Unit: M1a's suite identity + fixed-denominator scorer (<see cref="EvalSuite"/>). Pins the three invariants:
/// the manifest VERSION is content-derived and order-independent (any semantic task change = a NEW suite, so a
/// percentage claim always names what it measured); the cell universe is the FIXED DENOMINATOR (an errored or
/// never-reached cell is InfraUnknown, never dropped from the divisor); and the CURRENT seed corpus's version is
/// FROZEN — changing the corpus without consciously updating the pin is a visible decision, never a silent drift
/// under an unchanged headline number.
/// </summary>
[Trait("Category", "Unit")]
public class EvalSuiteTests
{
    // ── Version identity ───────────────────────────────────────────────────────────────

    [Fact]
    public void The_same_corpus_yields_the_same_version()
    {
        EvalSuite.ManifestFor(Corpus()).Version.ShouldBe(EvalSuite.ManifestFor(Corpus()).Version);
    }

    [Fact]
    public void Reordering_tasks_does_not_change_the_version()
    {
        var forward = EvalSuite.ManifestFor(Corpus());
        var reversed = EvalSuite.ManifestFor(Corpus().Reverse().ToList());

        reversed.Version.ShouldBe(forward.Version, "authoring order is not suite identity — the canonical form sorts by task id");
        reversed.Cells.ShouldBe(forward.Cells, "the cell universe is canonically ordered too");
    }

    [Theory]
    [InlineData("goal")]
    [InlineData("fixture")]
    [InlineData("mode")]
    [InlineData("timeout")]
    public void Changing_any_semantic_field_changes_the_version(string field)
    {
        var baseline = EvalSuite.ManifestFor(Corpus()).Version;

        var mutated = Corpus().ToList();
        mutated[0] = field switch
        {
            "goal" => mutated[0] with { Goal = "a DIFFERENT ask" },
            "fixture" => mutated[0] with { FixtureRef = "other-fixture" },
            "timeout" => mutated[0] with { TimeoutSeconds = 999 },
            _ => mutated[0] with { Modes = new[] { BenchmarkMode.HarnessCli } },
        };

        EvalSuite.ManifestFor(mutated).Version.ShouldNotBe(baseline, $"a changed {field} is a semantically different suite — comparing its rate to the old suite's would be a silent drift");
    }

    [Fact]
    public void The_shipped_seed_corpus_version_is_frozen()
    {
        // THE FREEZE (M1a): this literal is the shipped suite's identity. Any corpus change — a new task, a
        // reworded goal, a fixture edit reflected in these fields, a mode set change — MUST consciously update
        // this pin alongside re-baselining any recorded solve-rate expectations. A failing pin here is the
        // system telling you a percentage somewhere is about to be compared across different measurements.
        EvalSuite.ManifestFor(SeedBenchmarkCorpus.Tasks).Version.ShouldBe("sha256/corpus-v2:afa44be37e273f1a");
    }

    [Fact]
    public void A_duplicate_task_id_fails_loud_never_aliases()
    {
        var corpus = new[] { Task("t1"), Task("t1") };

        Should.Throw<ArgumentException>(() => EvalSuite.ManifestFor(corpus))
            .Message.ShouldContain("t1", customMessage: "the same strict-identity bar H2 set for plan subtask ids — a dup cell would alias every id-keyed read");
    }

    [Fact]
    public void A_duplicate_mode_on_one_task_fails_loud()
    {
        var corpus = new[] { Task("t1") with { Modes = new[] { BenchmarkMode.HarnessCli, BenchmarkMode.HarnessCli } } };

        Should.Throw<ArgumentException>(() => EvalSuite.ManifestFor(corpus));
    }

    // ── Fixed denominator classification ───────────────────────────────────────────────

    [Fact]
    public void Every_cell_is_classified_and_an_errored_cell_is_InfraUnknown_not_dropped()
    {
        var corpus = Corpus();
        var manifest = EvalSuite.ManifestFor(corpus);

        var results = new List<BenchmarkResult>
        {
            Result("t1", BenchmarkMode.HarnessCli, passed: true),
            Result("t1", BenchmarkMode.HarnessCliWithMcp, passed: false),
            // t2/HarnessCli errored; t2/HarnessCliWithMcp never reached — BOTH must still occupy their cells.
        };
        var errored = new List<CorpusBenchmarkError> { new() { TaskId = "t2", Mode = BenchmarkMode.HarnessCli, Error = "staging exploded" } };

        var cells = EvalSuite.Classify(manifest, results, errored);

        cells.Count.ShouldBe(manifest.Cells.Count, "the FIXED denominator — every suite cell classified, nothing dropped");
        cells.Single(c => c is { TaskId: "t1", Mode: BenchmarkMode.HarnessCli }).State.ShouldBe(CorpusCellState.Solved);
        cells.Single(c => c is { TaskId: "t1", Mode: BenchmarkMode.HarnessCliWithMcp }).State.ShouldBe(CorpusCellState.Unsolved);
        cells.Single(c => c is { TaskId: "t2", Mode: BenchmarkMode.HarnessCli }).State.ShouldBe(CorpusCellState.InfraUnknown);
        cells.Single(c => c is { TaskId: "t2", Mode: BenchmarkMode.HarnessCli }).Detail.ShouldBe("staging exploded");
        cells.Single(c => c is { TaskId: "t2", Mode: BenchmarkMode.HarnessCliWithMcp }).State.ShouldBe(CorpusCellState.InfraUnknown, "a never-reached cell proves nothing and still occupies its slot");
    }

    [Fact]
    public void The_score_divides_by_ALL_cells_and_health_isolates_the_instrument()
    {
        var cells = new[]
        {
            Cell(CorpusCellState.Solved), Cell(CorpusCellState.Solved), Cell(CorpusCellState.Unsolved), Cell(CorpusCellState.InfraUnknown),
        };

        var score = EvalSuite.Score(cells);

        score.Total.ShouldBe(4);
        score.SolveRateOverSuite.ShouldBe(0.5, "2 solved over ALL 4 cells — the infra-dead cell HURTS the rate, so a broken evaluator can never inflate capability");
        score.EvaluatorHealth.ShouldBe(0.75, "3 of 4 cells carry a real capability verdict");
    }

    [Fact]
    public void An_empty_suite_scores_zero_never_throws()
    {
        var score = EvalSuite.Score(Array.Empty<CorpusCellOutcome>());

        score.SolveRateOverSuite.ShouldBe(0);
        score.EvaluatorHealth.ShouldBe(0);
    }

    // ── Fixtures ────────────────────────────────────────────────────────────────────────

    private static IReadOnlyList<BenchmarkTask> Corpus() => new[]
    {
        Task("t1"),
        Task("t2"),
    };

    private static BenchmarkTask Task(string id) => new()
    {
        Id = id, Description = $"desc {id}", FixtureRef = $"fixture-{id}", Goal = $"solve {id}", Harness = "codex-cli",
        Grading = BenchmarkGradingKind.TestsPass, TestCommand = new[] { "sh", "check.sh" },
        Modes = new[] { BenchmarkMode.HarnessCli, BenchmarkMode.HarnessCliWithMcp },
    };

    private static BenchmarkResult Result(string taskId, BenchmarkMode mode, bool passed) => new()
    {
        TaskId = taskId, Mode = mode, RunStatus = Messages.Enums.AgentRunStatus.Succeeded, McpEndpointEnabled = false,
        Grade = new BenchmarkGrade { Passed = passed, Detail = passed ? "tests green" : "2 cases failed" },
        DurationSeconds = 1,
    };

    private static CorpusCellOutcome Cell(CorpusCellState state) => new()
    {
        TaskId = Guid.NewGuid().ToString("N"), Mode = BenchmarkMode.HarnessCli, State = state,
    };
}
