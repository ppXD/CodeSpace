using CodeSpace.Core.Services.Agents.Eval.Benchmark;
using CodeSpace.Core.Services.Agents.Eval.Benchmark.Graders;
using CodeSpace.Messages.Agents.Benchmark;
using Shouldly;

namespace CodeSpace.UnitTests.Agents.Benchmark;

/// <summary>
/// Pins the seed corpus shape + the mode-label map + the grader registry — the static, deterministic surface of
/// the instrument. The corpus is a SMALL seed (slice-1 scope), every task tests-pass-gradable; the mode labels
/// are the wire-visible scorecard row keys (Rule 8); the registry resolves the one built grader + refuses an
/// unbuilt follow-on kind loudly.
/// </summary>
[Trait("Category", "Unit")]
public class BenchmarkCorpusTests
{
    [Fact]
    public void Seed_corpus_is_a_small_3_to_5_task_set_all_tests_pass_gradable()
    {
        SeedBenchmarkCorpus.Tasks.Count.ShouldBeInRange(3, 5, "slice-1 ships a SMALL seed corpus, not a 20-task suite");

        SeedBenchmarkCorpus.Tasks.ShouldAllBe(t => t.Grading == BenchmarkGradingKind.TestsPass, "every seed task is graded by the objective tests-pass oracle");
        SeedBenchmarkCorpus.Tasks.ShouldAllBe(t => t.TestCommand.Count > 0, "every tests-pass task carries a test command for the grader to re-run");
        SeedBenchmarkCorpus.Tasks.ShouldAllBe(t => t.Modes.Count > 0, "every task names the modes to compare it across");
    }

    [Fact]
    public void Seed_corpus_task_ids_are_unique()
    {
        var ids = SeedBenchmarkCorpus.Tasks.Select(t => t.Id).ToList();

        ids.Distinct().Count().ShouldBe(ids.Count, "a task id is the scorecard + result row key, so it must be unique within the corpus");
    }

    [Theory]
    [InlineData(BenchmarkMode.HarnessCli, "bench:cli")]
    [InlineData(BenchmarkMode.HarnessCliWithMcp, "bench:cli-mcp")]
    [InlineData(BenchmarkMode.WorkflowMap, "bench:workflow-map")]
    public void Mode_label_is_the_pinned_bench_prefixed_row_key(BenchmarkMode mode, string expected)
    {
        // Renaming a label silently changes the operator-visible scorecard row + breaks a UI that filters on it.
        BenchmarkModeLabel.For(mode).ShouldBe(expected);
        expected.ShouldStartWith(BenchmarkModeLabel.Prefix);
    }

    [Fact]
    public void Mode_label_prefix_is_pinned()
    {
        BenchmarkModeLabel.Prefix.ShouldBe("bench:");
    }

    [Fact]
    public void Grader_registry_resolves_the_tests_pass_grader()
    {
        var registry = new BenchmarkGraderRegistry(new[] { new TestsPassGrader() });

        registry.Resolve(BenchmarkGradingKind.TestsPass).ShouldBeOfType<TestsPassGrader>();
    }

    [Fact]
    public void Grader_registry_refuses_an_unbuilt_follow_on_kind_loudly()
    {
        var registry = new BenchmarkGraderRegistry(new[] { new TestsPassGrader() });

        Should.Throw<InvalidOperationException>(() => registry.Resolve(BenchmarkGradingKind.LlmJudge))
            .Message.ShouldContain("follow-on", customMessage: "an unbuilt grader kind fails loudly, not silently");
    }

    [Fact]
    public void Grader_registry_rejects_duplicate_kinds()
    {
        Should.Throw<InvalidOperationException>(() => new BenchmarkGraderRegistry(new[] { new TestsPassGrader(), new TestsPassGrader() }));
    }
}
