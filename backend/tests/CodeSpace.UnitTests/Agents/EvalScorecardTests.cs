using CodeSpace.Core.Services.Agents.Eval;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Enums;
using Shouldly;

namespace CodeSpace.UnitTests.Agents;

/// <summary>
/// Pins the pure agent-run scorer: terminal-only scoring, per-harness grouping + sorted output, an overall
/// rollup, success-rate math, and nearest-rank latency percentiles over only the runs that have a duration.
/// This is the measurement that makes "is the agent working" a number, so its edges are nailed down.
/// </summary>
[Trait("Category", "Unit")]
public class EvalScorecardTests
{
    private static AgentRunOutcome Run(string harness, AgentRunStatus status, double? durationSeconds = null) =>
        new() { Harness = harness, Status = status, DurationSeconds = durationSeconds };

    [Fact]
    public void Empty_input_is_a_zeroed_overall_with_no_harness_rows()
    {
        var card = EvalScorecard.Compute(Array.Empty<AgentRunOutcome>());

        card.Harnesses.ShouldBeEmpty();
        card.Overall.Harness.ShouldBe(EvalScorecard.OverallLabel);
        card.Overall.Total.ShouldBe(0);
        card.Overall.Succeeded.ShouldBe(0);
        card.Overall.SuccessRate.ShouldBe(0);
        card.Overall.P50DurationSeconds.ShouldBeNull();
        card.Overall.P95DurationSeconds.ShouldBeNull();
    }

    [Fact]
    public void In_flight_runs_are_excluded_from_the_totals()
    {
        var card = EvalScorecard.Compute(new[]
        {
            Run("codex-cli", AgentRunStatus.Succeeded),
            Run("codex-cli", AgentRunStatus.Succeeded),
            Run("codex-cli", AgentRunStatus.Succeeded),
            Run("codex-cli", AgentRunStatus.Queued),
            Run("codex-cli", AgentRunStatus.Running),
        });

        card.Overall.Total.ShouldBe(3, "only terminal runs are scored");
        card.Overall.Succeeded.ShouldBe(3);
        card.Overall.SuccessRate.ShouldBe(1.0);
    }

    [Fact]
    public void Success_rate_counts_only_succeeded_against_all_terminal_runs()
    {
        var card = EvalScorecard.Compute(new[]
        {
            Run("h", AgentRunStatus.Succeeded), Run("h", AgentRunStatus.Succeeded), Run("h", AgentRunStatus.Succeeded),
            Run("h", AgentRunStatus.Failed), Run("h", AgentRunStatus.TimedOut),
        });

        card.Overall.Total.ShouldBe(5);
        card.Overall.Succeeded.ShouldBe(3);
        card.Overall.SuccessRate.ShouldBe(0.6);
    }

    [Fact]
    public void Runs_are_grouped_per_harness_sorted_with_an_overall_rollup()
    {
        var card = EvalScorecard.Compute(new[]
        {
            Run("zebra", AgentRunStatus.Succeeded),
            Run("alpha", AgentRunStatus.Succeeded), Run("alpha", AgentRunStatus.Failed),
        });

        card.Harnesses.Count.ShouldBe(2);
        card.Harnesses[0].Harness.ShouldBe("alpha", "harness rows are sorted by name");
        card.Harnesses[0].Total.ShouldBe(2);
        card.Harnesses[0].Succeeded.ShouldBe(1);
        card.Harnesses[1].Harness.ShouldBe("zebra");
        card.Harnesses[1].SuccessRate.ShouldBe(1.0);

        card.Overall.Total.ShouldBe(3);
        card.Overall.Succeeded.ShouldBe(2);
        card.Overall.SuccessRate.ShouldBe(2.0 / 3.0, 1e-9);
    }

    [Fact]
    public void Latency_percentiles_use_nearest_rank_over_durations_present()
    {
        var card = EvalScorecard.Compute(Enumerable.Range(1, 10)
            .Select(i => Run("h", AgentRunStatus.Succeeded, i * 10.0))
            .ToArray());

        // durations 10..100; nearest-rank p50 → rank ceil(0.5*10)=5 → 50; p95 → rank ceil(9.5)=10 → 100.
        card.Overall.P50DurationSeconds.ShouldBe(50);
        card.Overall.P95DurationSeconds.ShouldBe(100);
    }

    [Fact]
    public void Runs_without_a_duration_count_toward_totals_but_not_percentiles()
    {
        var card = EvalScorecard.Compute(new[]
        {
            Run("h", AgentRunStatus.Succeeded, 4),
            Run("h", AgentRunStatus.Succeeded, 8),
            Run("h", AgentRunStatus.Succeeded, durationSeconds: null),
        });

        card.Overall.Total.ShouldBe(3, "the duration-less run still counts in the total");
        card.Overall.P50DurationSeconds.ShouldBe(4);
        card.Overall.P95DurationSeconds.ShouldBe(8);
    }

    [Fact]
    public void Is_deterministic()
    {
        var runs = new[] { Run("a", AgentRunStatus.Succeeded, 3), Run("b", AgentRunStatus.Failed, 9), Run("a", AgentRunStatus.TimedOut, 1) };

        // Compare the serialized shape (record equality over the List member would be reference-based).
        System.Text.Json.JsonSerializer.Serialize(EvalScorecard.Compute(runs))
            .ShouldBe(System.Text.Json.JsonSerializer.Serialize(EvalScorecard.Compute(runs)));
    }
}
