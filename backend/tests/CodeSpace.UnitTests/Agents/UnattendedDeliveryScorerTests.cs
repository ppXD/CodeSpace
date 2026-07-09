using CodeSpace.Core.Services.Agents.Eval;
using CodeSpace.Messages.Agents;
using Shouldly;

namespace CodeSpace.UnitTests.Agents;

/// <summary>
/// Pins the pure unattended-delivery scorer — the path-to-intelligence north-star measurement ("task in →
/// merged/published artifact out with zero human touches"). Covers the headline bit's three AND-ed conditions
/// (solved / delivered / zero touches) each in isolation, the cross-run rate math, and the fail-open cost
/// qualifier (null distinct from a real $0). Deterministic + DB-free, so every edge is nailed down without Postgres.
/// </summary>
[Trait("Category", "Unit")]
public class UnattendedDeliveryScorerTests
{
    private static UnattendedDeliveryRunOutcome Run(bool solved, bool delivered, int humanTouches = 0, decimal? costUsd = null, Guid? id = null) => new()
    {
        WorkflowRunId = id ?? Guid.NewGuid(),
        Solved = solved,
        Delivered = delivered,
        HumanTouches = humanTouches,
        CostUsd = costUsd,
    };

    // ─── Per-run scoring — the headline bit ────────────────────────────────────────

    [Fact]
    public void Solved_delivered_zero_touches_is_the_only_way_to_score_unattended()
    {
        UnattendedDeliveryScorer.Score(Run(solved: true, delivered: true, humanTouches: 0)).UnattendedSolvedWithDelivery.ShouldBeTrue();
    }

    [Fact]
    public void Solved_but_not_delivered_is_not_unattended()
    {
        UnattendedDeliveryScorer.Score(Run(solved: true, delivered: false)).UnattendedSolvedWithDelivery.ShouldBeFalse("the diff never left the sandbox — nothing was actually shipped");
    }

    [Fact]
    public void Delivered_but_not_solved_is_not_unattended()
    {
        UnattendedDeliveryScorer.Score(Run(solved: false, delivered: true)).UnattendedSolvedWithDelivery.ShouldBeFalse("a pushed branch whose acceptance check never passed is not a solve");
    }

    [Fact]
    public void Solved_and_delivered_but_with_a_human_touch_is_not_unattended()
    {
        UnattendedDeliveryScorer.Score(Run(solved: true, delivered: true, humanTouches: 1)).UnattendedSolvedWithDelivery.ShouldBeFalse("even one ask_human or approval-parked tool call breaks 'unattended' regardless of the eventual outcome");
    }

    [Fact]
    public void Neither_solved_nor_delivered_is_not_unattended()
    {
        UnattendedDeliveryScorer.Score(Run(solved: false, delivered: false)).UnattendedSolvedWithDelivery.ShouldBeFalse();
    }

    [Fact]
    public void Score_carries_the_run_id_and_raw_fields_through_verbatim()
    {
        var id = Guid.NewGuid();
        var score = UnattendedDeliveryScorer.Score(Run(solved: true, delivered: true, humanTouches: 3, costUsd: 4.25m, id: id));

        score.WorkflowRunId.ShouldBe(id);
        score.Solved.ShouldBeTrue();
        score.Delivered.ShouldBeTrue();
        score.HumanTouches.ShouldBe(3);
        score.CostUsd.ShouldBe(4.25m);
    }

    // ─── Cross-run roll-up ──────────────────────────────────────────────────────────

    [Fact]
    public void An_empty_run_list_yields_a_zeroed_rollup_with_no_division_by_zero()
    {
        var card = UnattendedDeliveryScorer.Compute(Array.Empty<UnattendedDeliveryRunOutcome>());

        card.Rollup.TotalRuns.ShouldBe(0);
        card.Rollup.UnattendedSolveWithDeliveryRate.ShouldBe(0);
        card.Rollup.SolveRate.ShouldBe(0);
        card.Rollup.DeliveryRate.ShouldBe(0);
        card.Rollup.AvgHumanTouches.ShouldBe(0);
        card.Rollup.TotalCostUsd.ShouldBeNull();
        card.Rollup.UnknownCostRuns.ShouldBe(0);
        card.Runs.ShouldBeEmpty();
    }

    [Fact]
    public void The_north_star_rate_is_unattended_runs_over_total_runs()
    {
        var runs = new[]
        {
            Run(solved: true, delivered: true, humanTouches: 0),   // unattended
            Run(solved: true, delivered: true, humanTouches: 0),   // unattended
            Run(solved: true, delivered: true, humanTouches: 1),   // solved+delivered but touched — NOT unattended
            Run(solved: false, delivered: false),                  // neither
        };

        var card = UnattendedDeliveryScorer.Compute(runs);

        card.Rollup.TotalRuns.ShouldBe(4);
        card.Rollup.UnattendedSolvedWithDeliveryRuns.ShouldBe(2);
        card.Rollup.UnattendedSolveWithDeliveryRate.ShouldBe(0.5);
        card.Rollup.SolvedRuns.ShouldBe(3);
        card.Rollup.SolveRate.ShouldBe(0.75);
        card.Rollup.DeliveredRuns.ShouldBe(3);
        card.Rollup.DeliveryRate.ShouldBe(0.75);
    }

    [Fact]
    public void Avg_human_touches_is_over_ALL_runs_not_just_the_touched_ones()
    {
        var runs = new[]
        {
            Run(solved: true, delivered: true, humanTouches: 0),
            Run(solved: true, delivered: true, humanTouches: 4),
        };

        UnattendedDeliveryScorer.Compute(runs).Rollup.AvgHumanTouches.ShouldBe(2.0, "(0 + 4) / 2 over the full population, not just the touched run");
    }

    [Fact]
    public void Total_cost_sums_only_the_priced_runs_and_is_null_when_none_are_priceable()
    {
        var allUnknown = new[] { Run(solved: true, delivered: true, costUsd: null), Run(solved: false, delivered: false, costUsd: null) };
        UnattendedDeliveryScorer.Compute(allUnknown).Rollup.TotalCostUsd.ShouldBeNull("nothing in the window was priceable — null is distinct from a real $0");
        UnattendedDeliveryScorer.Compute(allUnknown).Rollup.UnknownCostRuns.ShouldBe(2);

        var mixed = new[] { Run(solved: true, delivered: true, costUsd: 1.5m), Run(solved: false, delivered: false, costUsd: null), Run(solved: true, delivered: true, costUsd: 2.5m) };
        var mixedRollup = UnattendedDeliveryScorer.Compute(mixed).Rollup;
        mixedRollup.TotalCostUsd.ShouldBe(4.0m, "sums ONLY the two priced runs — the unpriceable one contributes nothing to the sum");
        mixedRollup.UnknownCostRuns.ShouldBe(1);
    }

    [Fact]
    public void A_priced_zero_cost_run_is_a_real_zero_not_unknown()
    {
        var runs = new[] { Run(solved: true, delivered: true, costUsd: 0m) };

        var rollup = UnattendedDeliveryScorer.Compute(runs).Rollup;
        rollup.TotalCostUsd.ShouldBe(0m, "a priced (known) $0 must stay a real zero, distinct from unpriceable");
        rollup.UnknownCostRuns.ShouldBe(0);
    }

    [Fact]
    public void Runs_are_returned_in_the_input_order_verbatim()
    {
        var idFirst = Guid.NewGuid();
        var idSecond = Guid.NewGuid();
        var runs = new[] { Run(solved: true, delivered: true, id: idFirst), Run(solved: false, delivered: false, id: idSecond) };

        var card = UnattendedDeliveryScorer.Compute(runs);

        card.Runs[0].WorkflowRunId.ShouldBe(idFirst);
        card.Runs[1].WorkflowRunId.ShouldBe(idSecond);
    }
}
