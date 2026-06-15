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
    private static BenchmarkResult Result(BenchmarkMode mode, bool passed, AgentRunStatus runStatus = AgentRunStatus.Succeeded, double? duration = null) => new()
    {
        TaskId = "t",
        Mode = mode,
        AgentRunId = Guid.NewGuid(),
        RunStatus = runStatus,
        DurationSeconds = duration,
        Grade = new BenchmarkGrade { Passed = passed, Detail = passed ? "tests-passed" : "tests-failed" },
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
}
