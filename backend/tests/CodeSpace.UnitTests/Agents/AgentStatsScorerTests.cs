using CodeSpace.Core.Services.Agents.Eval;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Enums;
using Shouldly;

namespace CodeSpace.UnitTests.Agents;

/// <summary>
/// Pins the pure PER-AGENT scorer: grouping by persona (sorted), terminal-only success/latency, the recent-outcome
/// sparkline (last N, oldest→newest, in-flight included), the last-active stamp, and the spend roll-up's three-state
/// cost (summed / unknown-qualified / all-unknown-is-null, in-flight excluded). This is the evidence the redesigned
/// Agents roster shows per row, so its edges are nailed down the same way <see cref="EvalScorecardTests"/> pins the
/// harness scorer.
/// </summary>
[Trait("Category", "Unit")]
public class AgentStatsScorerTests
{
    private static readonly DateTimeOffset T0 = new(2026, 7, 1, 0, 0, 0, TimeSpan.Zero);

    private static AgentRunStatSample Run(
        Guid agent, AgentRunStatus status, int minute = 0,
        double? durationSeconds = null, bool costEligible = false, decimal? cost = null) =>
        new()
        {
            AgentDefinitionId = agent,
            Status = status,
            DurationSeconds = durationSeconds,
            CostEligible = costEligible,
            Cost = cost,
            CreatedAt = T0.AddMinutes(minute),
        };

    [Fact]
    public void Empty_input_is_an_empty_roster()
    {
        AgentStatsScorer.Compute(Array.Empty<AgentRunStatSample>()).Agents.ShouldBeEmpty();
    }

    [Fact]
    public void Runs_are_grouped_per_agent_sorted_by_id()
    {
        // A Guid whose string sorts after B's, to prove ordering is by the Guid, not insertion order.
        var a = new Guid("00000000-0000-0000-0000-0000000000ff");
        var b = new Guid("00000000-0000-0000-0000-000000000001");

        var card = AgentStatsScorer.Compute(new[]
        {
            Run(a, AgentRunStatus.Succeeded),
            Run(b, AgentRunStatus.Succeeded), Run(b, AgentRunStatus.Failed),
        });

        card.Agents.Count.ShouldBe(2);
        card.Agents[0].AgentDefinitionId.ShouldBe(b, "agent rows are sorted by id");
        card.Agents[0].Total.ShouldBe(2);
        card.Agents[0].Succeeded.ShouldBe(1);
        card.Agents[1].AgentDefinitionId.ShouldBe(a);
    }

    [Fact]
    public void In_flight_runs_are_excluded_from_totals_but_kept_in_the_sparkline()
    {
        var a = Guid.NewGuid();

        var stat = AgentStatsScorer.Compute(new[]
        {
            Run(a, AgentRunStatus.Failed, minute: 1),
            Run(a, AgentRunStatus.Succeeded, minute: 2),
            Run(a, AgentRunStatus.Running, minute: 3),
        }).Agents.Single();

        stat.Total.ShouldBe(2, "only terminal runs are scored");
        stat.Succeeded.ShouldBe(1);
        stat.SuccessRate.ShouldBe(0.5);
        stat.RecentOutcomes.ShouldBe(new[] { AgentRunStatus.Failed, AgentRunStatus.Succeeded, AgentRunStatus.Running },
            "the sparkline is oldest→newest and keeps the in-flight run");
        stat.LastRunAt.ShouldBe(T0.AddMinutes(3), "the last-active stamp is the most recent run of any status");
    }

    [Fact]
    public void Needs_review_counts_as_a_terminal_non_success()
    {
        var a = Guid.NewGuid();

        var stat = AgentStatsScorer.Compute(new[]
        {
            Run(a, AgentRunStatus.Succeeded), Run(a, AgentRunStatus.Succeeded),
            Run(a, AgentRunStatus.NeedsReview),
        }).Agents.Single();

        stat.Total.ShouldBe(3, "NeedsReview is terminal — counted in the denominator");
        stat.Succeeded.ShouldBe(2, "NeedsReview is not a success");
        stat.SuccessRate.ShouldBe(2.0 / 3.0, 1e-9);
    }

    [Fact]
    public void Latency_percentiles_use_nearest_rank_over_durations_present()
    {
        var a = Guid.NewGuid();

        var stat = AgentStatsScorer.Compute(Enumerable.Range(1, 10)
            .Select(i => Run(a, AgentRunStatus.Succeeded, durationSeconds: i * 10.0))
            .ToArray()).Agents.Single();

        // durations 10..100; nearest-rank p50 → rank ceil(0.5*10)=5 → 50; p95 → rank ceil(9.5)=10 → 100.
        stat.P50DurationSeconds.ShouldBe(50);
        stat.P95DurationSeconds.ShouldBe(100);
    }

    [Fact]
    public void Cost_sums_priced_eligible_runs_and_qualifies_the_unpriceable()
    {
        var a = Guid.NewGuid();

        var stat = AgentStatsScorer.Compute(new[]
        {
            Run(a, AgentRunStatus.Succeeded, costEligible: true, cost: 1.50m),
            Run(a, AgentRunStatus.Succeeded, costEligible: true, cost: 0.44m),
            Run(a, AgentRunStatus.Failed, costEligible: true, cost: null),      // eligible but unpriceable → unknown
            Run(a, AgentRunStatus.Running, costEligible: false, cost: null),    // in-flight → excluded from cost entirely
        }).Agents.Single();

        stat.EstimatedCostUsd.ShouldBe(1.94m);
        stat.UnknownCostRuns.ShouldBe(1, "only the eligible-but-unpriceable run is unknown; the in-flight run is not");
    }

    [Fact]
    public void A_priceable_zero_cost_run_reports_a_real_zero_not_null()
    {
        var a = Guid.NewGuid();

        var stat = AgentStatsScorer.Compute(new[]
        {
            Run(a, AgentRunStatus.Succeeded, costEligible: true, cost: 0m),
        }).Agents.Single();

        stat.EstimatedCostUsd.ShouldBe(0m, "a priceable run that cost $0 is a real $0 — NOT null/unknown (the other half of the null-distinct-from-$0 contract)");
        stat.UnknownCostRuns.ShouldBe(0);
    }

    [Fact]
    public void Cost_is_null_when_no_eligible_run_was_priceable()
    {
        var a = Guid.NewGuid();

        var stat = AgentStatsScorer.Compute(new[]
        {
            Run(a, AgentRunStatus.Failed, costEligible: true, cost: null),
            Run(a, AgentRunStatus.Failed, costEligible: true, cost: null),
        }).Agents.Single();

        stat.EstimatedCostUsd.ShouldBeNull("null spend is distinct from a real $0 — nothing could be priced");
        stat.UnknownCostRuns.ShouldBe(2);
    }

    [Fact]
    public void Sparkline_keeps_only_the_most_recent_runs_still_oldest_to_newest()
    {
        var a = Guid.NewGuid();

        // 12 runs at increasing times. The two OLDEST (minutes 1-2) get unique sentinel statuses that appear nowhere
        // else, so the assertions fail unless the cap drops the oldest end (not the newest) AND the order is ascending.
        // A mutant that kept the 10 oldest, or Take()-d before ordering, would surface Cancelled/TimedOut and be caught.
        var runs = new List<AgentRunStatSample>
        {
            Run(a, AgentRunStatus.Cancelled, minute: 1),   // oldest — must be DROPPED
            Run(a, AgentRunStatus.TimedOut, minute: 2),    // 2nd oldest — must be DROPPED
        };
        for (var i = 3; i <= 12; i++)
            runs.Add(Run(a, i % 2 == 0 ? AgentRunStatus.Succeeded : AgentRunStatus.Failed, minute: i));

        var stat = AgentStatsScorer.Compute(runs).Agents.Single();

        // The kept window is minutes 3..12 in ascending order: odd→Failed, even→Succeeded.
        stat.RecentOutcomes.ShouldBe(new[]
        {
            AgentRunStatus.Failed, AgentRunStatus.Succeeded, AgentRunStatus.Failed, AgentRunStatus.Succeeded, AgentRunStatus.Failed,
            AgentRunStatus.Succeeded, AgentRunStatus.Failed, AgentRunStatus.Succeeded, AgentRunStatus.Failed, AgentRunStatus.Succeeded,
        });
        stat.RecentOutcomes.ShouldNotContain(AgentRunStatus.Cancelled, "the oldest run is dropped when the history exceeds the cap");
        stat.RecentOutcomes.ShouldNotContain(AgentRunStatus.TimedOut, "the 2nd-oldest run is dropped too");
    }

    [Fact]
    public void Is_deterministic()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var runs = new[]
        {
            Run(a, AgentRunStatus.Succeeded, minute: 1, durationSeconds: 3, costEligible: true, cost: 0.10m),
            Run(b, AgentRunStatus.Failed, minute: 2, durationSeconds: 9, costEligible: true, cost: null),
            Run(a, AgentRunStatus.TimedOut, minute: 3, durationSeconds: 1, costEligible: true, cost: 0.20m),
        };

        System.Text.Json.JsonSerializer.Serialize(AgentStatsScorer.Compute(runs))
            .ShouldBe(System.Text.Json.JsonSerializer.Serialize(AgentStatsScorer.Compute(runs)));
    }
}
