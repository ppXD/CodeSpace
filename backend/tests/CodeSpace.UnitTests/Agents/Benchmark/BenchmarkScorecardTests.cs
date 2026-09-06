using CodeSpace.Core.Services.Agents.Eval;
using CodeSpace.Core.Services.Agents.Eval.Benchmark;
using CodeSpace.Messages.Agents.Benchmark;
using CodeSpace.Messages.Enums;
using Shouldly;

namespace CodeSpace.UnitTests.Agents.Benchmark;

/// <summary>
/// Pins the benchmark scorecard projection: a set of <see cref="BenchmarkResult"/> reduces — through the SAME
/// pure <see cref="EvalScorecard"/> the team-history scorecard uses — into per-MODE rows whose success rate is
/// the SOLVE rate (the objective grade), not mere run-completion. The honest twist (a Succeeded run that fails
/// the grade is a non-success row) is nailed down, because it is the whole reason the instrument exists.
/// </summary>
[Trait("Category", "Unit")]
public class BenchmarkScorecardTests
{
    private static BenchmarkResult Result(BenchmarkMode mode, bool passed, AgentRunStatus runStatus = AgentRunStatus.Succeeded, double? duration = null, int respawns = 0) => new()
    {
        TaskId = "t",
        Mode = mode,
        AgentRunId = Guid.NewGuid(),
        RunStatus = runStatus,
        DurationSeconds = duration,
        Grade = new BenchmarkGrade { Passed = passed, Detail = passed ? "tests-passed" : "tests-failed" },
        McpFullCatalog = mode == BenchmarkMode.HarnessCliWithMcp,
        FormatFaultRespawns = respawns,
    };

    [Fact]
    public void Rows_group_per_mode_with_the_bench_prefixed_label()
    {
        var card = BenchmarkScorecard.Compute(new[]
        {
            Result(BenchmarkMode.HarnessCli, passed: true),
            Result(BenchmarkMode.HarnessCliWithMcp, passed: true),
        });

        card.Harnesses.Select(h => h.Harness).ShouldBe(new[] { "bench:cli", "bench:cli-mcp" }, "one comparable row per mode, sorted, bench-prefixed");
    }

    [Fact]
    public void Success_rate_is_the_solve_rate_not_the_run_completion_rate()
    {
        // Both runs Succeeded, but only one SOLVED the task — the scorecard must report 0.5, the honest signal.
        var card = BenchmarkScorecard.Compute(new[]
        {
            Result(BenchmarkMode.HarnessCli, passed: true, runStatus: AgentRunStatus.Succeeded),
            Result(BenchmarkMode.HarnessCli, passed: false, runStatus: AgentRunStatus.Succeeded),
        });

        var row = card.Harnesses.Single();
        row.Total.ShouldBe(2);
        row.Succeeded.ShouldBe(1, "a Succeeded run that FAILED the grade is scored a non-success — solving is what's measured");
        row.SuccessRate.ShouldBe(0.5);
    }

    [Fact]
    public void Modes_are_compared_side_by_side_so_better_is_a_number()
    {
        // cli: 1/2 solved; cli-mcp: 2/2 solved — the comparison the instrument exists to produce.
        var card = BenchmarkScorecard.Compute(new[]
        {
            Result(BenchmarkMode.HarnessCli, passed: true),
            Result(BenchmarkMode.HarnessCli, passed: false),
            Result(BenchmarkMode.HarnessCliWithMcp, passed: true),
            Result(BenchmarkMode.HarnessCliWithMcp, passed: true),
        });

        var cli = card.Harnesses.Single(h => h.Harness == "bench:cli");
        var mcp = card.Harnesses.Single(h => h.Harness == "bench:cli-mcp");

        cli.SuccessRate.ShouldBe(0.5);
        mcp.SuccessRate.ShouldBe(1.0);
        mcp.SuccessRate.ShouldBeGreaterThan(cli.SuccessRate, "cli+mcp beat cli on this corpus — and it's a number, not an assertion");
    }

    [Fact]
    public void Duration_carries_through_to_the_latency_percentiles()
    {
        var card = BenchmarkScorecard.Compute(Enumerable.Range(1, 10)
            .Select(i => Result(BenchmarkMode.HarnessCli, passed: true, duration: i * 10.0))
            .ToArray());

        var row = card.Harnesses.Single();
        row.P50DurationSeconds.ShouldBe(50);
        row.P95DurationSeconds.ShouldBe(100);
    }

    [Fact]
    public void A_timed_out_failing_run_keeps_its_terminal_status_as_a_scored_non_success()
    {
        var card = BenchmarkScorecard.Compute(new[]
        {
            Result(BenchmarkMode.HarnessCli, passed: false, runStatus: AgentRunStatus.TimedOut),
        });

        var row = card.Harnesses.Single();
        row.Total.ShouldBe(1, "a timed-out run is terminal, so it's scored");
        row.Succeeded.ShouldBe(0);
    }

    // ── The gateway-health tally reported NEXT TO the rate (never inside it) ──

    [Fact]
    public void The_tally_separates_how_often_the_gateway_was_repaired_from_how_many_solves_the_repair_produced()
    {
        // The reader's problem this exists for: the run solved 2 cells and needed the repair on 2 cells, but only ONE
        // of the solves came out of a respawn. The mitigation runs with extended thinking DISABLED — a materially
        // different configuration from the one the corpus claims to measure — so a line reporting only the respawn
        // count leaves "how much of solved=2 was measured under the degraded config?" unanswerable.
        var tally = BenchmarkScorecard.TallyFormatFaults(new[]
        {
            Result(BenchmarkMode.HarnessCli, passed: true),                  // a clean solve — the declared configuration
            Result(BenchmarkMode.HarnessCli, passed: true, respawns: 1),     // solved ONLY after the repair
            Result(BenchmarkMode.HarnessCli, passed: false, respawns: 1),    // repaired and still unsolved
        });

        tally.Respawns.ShouldBe(2, "two cells needed the repair");
        tally.SolvedAfterMitigation.ShouldBe(1, "…but only ONE of the run's solves was produced with extended thinking disabled");
        tally.SolvedAfterMitigation.ShouldNotBe(tally.Respawns, "neither count is derivable from the other — which is exactly why both are reported");
    }

    [Fact]
    public void A_run_the_gateway_never_mangled_tallies_zero_on_both_counts()
    {
        var tally = BenchmarkScorecard.TallyFormatFaults(new[]
        {
            Result(BenchmarkMode.HarnessCli, passed: true),
            Result(BenchmarkMode.HarnessCliWithMcp, passed: false),
        });

        tally.Respawns.ShouldBe(0);
        tally.SolvedAfterMitigation.ShouldBe(0, "no repair was bought, so no solve can be attributed to one");
    }

    [Fact]
    public void The_tally_renders_under_ONE_wording_every_lane_reports_it_by()
    {
        // Three real-model lanes print this verbatim (blessed corpus × 2 lines, extended corpus, qualification
        // rehearsal). Pinning the rendering keeps two runs comparable under the same two names instead of three
        // hand-written interpolations drifting apart.
        new FormatFaultTally { Respawns = 9, SolvedAfterMitigation = 4 }.ToString()
            .ShouldBe("formatFaultRespawns=9, solvedAfterMitigation=4");
    }
}
