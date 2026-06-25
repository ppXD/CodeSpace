using CodeSpace.Core.Services.Agents.Eval.Benchmark;
using CodeSpace.Core.Services.Agents.Eval.Benchmark.Graders;
using CodeSpace.Messages.Agents.Benchmark;
using Shouldly;

namespace CodeSpace.UnitTests.Agents.Benchmark;

/// <summary>
/// Pins the seed corpus shape + the mode-label map + the grader registry — the static, deterministic surface of
/// the instrument. The corpus is a CURATED two-tier seed (an easy one-edit tier + a harder reasoning tier so a live
/// model's solve-rate differentiates), every task tests-pass-gradable; the mode labels are the wire-visible scorecard
/// row keys (Rule 8); the registry resolves the one built grader + refuses an unbuilt follow-on kind loudly.
/// </summary>
[Trait("Category", "Unit")]
public class BenchmarkCorpusTests
{
    [Fact]
    public void Seed_corpus_is_a_curated_two_tier_set_all_tests_pass_gradable()
    {
        SeedBenchmarkCorpus.Tasks.Count.ShouldBeInRange(8, 20, "a curated two-tier seed corpus (easy + harder), not a one-edit-only set nor a sprawling auto-generated suite");

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

    [Fact]
    public void Seed_corpus_ships_only_the_modes_the_single_run_runner_can_drive()
    {
        // WorkflowMap is reserved + engine-driven; the single-run runner throws for it, so listing it here would make
        // iterating the shipped corpus throw on every task. The corpus must be runnable end-to-end.
        SeedBenchmarkCorpus.DefaultModes.ShouldBe(new[] { BenchmarkMode.HarnessCli, BenchmarkMode.HarnessCliWithMcp });
        SeedBenchmarkCorpus.DefaultModes.ShouldNotContain(BenchmarkMode.WorkflowMap, "WorkflowMap is reserved/not-yet-wired — it must not be in the shipped corpus");
    }

    [Fact]
    public void Every_seed_fixture_ref_is_materialisable()
    {
        // Closes the "dead data" gap: each FixtureRef the corpus names must be one the stager knows how to write,
        // so a corpus task can actually be staged + run, not just described. (Staging + check exit-code is exercised
        // against a real runner in SeedBenchmarkFixturesTests.)
        var dir = Path.Combine(Path.GetTempPath(), "cs-bench-corpus-" + Guid.NewGuid().ToString("N"));
        try
        {
            foreach (var task in SeedBenchmarkCorpus.Tasks)
                Should.NotThrow(() => SeedBenchmarkFixtures.Stage(task.FixtureRef, Path.Combine(dir, task.FixtureRef)),
                    customMessage: $"the corpus names fixture '{task.FixtureRef}' but the stager can't materialise it");
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* best-effort */ }
        }
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
