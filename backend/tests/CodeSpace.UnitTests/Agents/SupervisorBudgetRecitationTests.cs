using CodeSpace.Core.Services.Supervisor.Deciders;
using Shouldly;

namespace CodeSpace.UnitTests.Agents;

/// <summary>Pins <see cref="SupervisorBudgetRecitation"/> (P3.5) — the shared budget-summary renderer for the decider's prompt AND the cost-cap stop detail.</summary>
public sealed class SupervisorBudgetRecitationTests
{
    private static readonly IReadOnlyDictionary<string, decimal> NoBrainSpend = new Dictionary<string, decimal>();

    [Fact]
    public void Render_is_null_when_no_cap_and_nothing_has_been_spent()
    {
        SupervisorBudgetRecitation.Render(null, agentExecutionSpendUsd: 0m, brainPlaneSpendUsd: 0m, NoBrainSpend).ShouldBeNull("a fresh uncapped run's prompt stays byte-identical — no budget section at all");
    }

    [Fact]
    public void Render_shows_spend_so_far_on_an_uncapped_run_that_has_spent_something()
    {
        // D6: the null-when-uncapped rule hid REALIZED spend from every uncapped run's brain — the common case.
        // The cap line is what needs a cap; the spend figure does not, and a brain that cannot see what it has
        // burned cannot moderate itself at all.
        // COPY pin only. That a brain-lane figure can REACH this renderer on an uncapped run is a property of the
        // rehydrate fold, not of this pure function — a fixture handing one in directly would stay green even if
        // the fold were still cap-gated. That half is pinned end to end by
        // ModelPricingUnderCapFlowTests.An_UNCAPPED_runs_own_prompt_recites_the_brain_lane_the_fold_produced.
        var text = SupervisorBudgetRecitation.Render(null, agentExecutionSpendUsd: 5m, brainPlaneSpendUsd: 1.25m, new Dictionary<string, decimal> { ["supervisor.decision"] = 1.25m });

        text.ShouldNotBeNull();
        text.ShouldStartWith(SupervisorBudgetRecitation.UncappedHeader);
        text!.ShouldContain("$6.25 spent so far — agent execution $5.00, supervisor.decision $1.25");
        text.ShouldNotContain("cap", Case.Insensitive, "there is no cap to recite — inventing one would be a lie the model plans against");
    }

    [Fact]
    public void Render_includes_the_pinned_header_and_the_summary_line_when_capped()
    {
        var text = SupervisorBudgetRecitation.Render(10m, agentExecutionSpendUsd: 3m, brainPlaneSpendUsd: 1m, NoBrainSpend);

        text.ShouldNotBeNull();
        text.ShouldStartWith(SupervisorBudgetRecitation.Header);
        text!.ShouldContain("$4.00 spent of $10.00 cap ($6.00 remaining)");
    }

    [Fact]
    public void Summary_reports_remaining_budget_under_the_cap()
    {
        SupervisorBudgetRecitation.Summary(10m, agentExecutionSpendUsd: 3m, brainPlaneSpendUsd: 2m, NoBrainSpend)
            .ShouldBe("$5.00 spent of $10.00 cap ($5.00 remaining) — agent execution $3.00");
    }

    [Fact]
    public void Summary_reports_OVER_when_spend_exceeds_the_cap()
    {
        SupervisorBudgetRecitation.Summary(10m, agentExecutionSpendUsd: 8m, brainPlaneSpendUsd: 4.50m, NoBrainSpend)
            .ShouldBe("$12.50 spent of $10.00 cap ($2.50 OVER) — agent execution $8.00");
    }

    [Fact]
    public void Summary_reports_exactly_at_cap_as_zero_remaining_not_over()
    {
        SupervisorBudgetRecitation.Summary(10m, agentExecutionSpendUsd: 10m, brainPlaneSpendUsd: 0m, NoBrainSpend)
            .ShouldBe("$10.00 spent of $10.00 cap ($0.00 remaining) — agent execution $10.00");
    }

    [Fact]
    public void Summary_appends_the_per_lane_breakdown_strongest_first()
    {
        var byKind = new Dictionary<string, decimal>
        {
            ["critic.review"] = 1.10m,
            ["supervisor.decision"] = 3.20m,
            ["grader.acceptance"] = 0.40m,
        };

        SupervisorBudgetRecitation.Summary(20m, agentExecutionSpendUsd: 6.84m, brainPlaneSpendUsd: 4.70m, byKind)
            .ShouldBe("$11.54 spent of $20.00 cap ($8.46 remaining) — agent execution $6.84, supervisor.decision $3.20, critic.review $1.10, grader.acceptance $0.40");
    }

    [Fact]
    public void Summary_omits_a_zero_spend_lane_never_a_noisy_dollar_zero_entry()
    {
        SupervisorBudgetRecitation.Summary(10m, agentExecutionSpendUsd: 0m, brainPlaneSpendUsd: 2m, new Dictionary<string, decimal> { ["supervisor.decision"] = 2m })
            .ShouldBe("$2.00 spent of $10.00 cap ($8.00 remaining) — supervisor.decision $2.00");
    }

    [Fact]
    public void Summary_with_zero_spend_everywhere_shows_only_the_headline()
    {
        SupervisorBudgetRecitation.Summary(10m, agentExecutionSpendUsd: 0m, brainPlaneSpendUsd: 0m, NoBrainSpend)
            .ShouldBe("$0.00 spent of $10.00 cap ($10.00 remaining)");
    }

    [Fact]
    public void Summary_breaks_a_lane_tie_alphabetically_for_determinism()
    {
        var byKind = new Dictionary<string, decimal>
        {
            ["critic.review"] = 1m,
            ["grader.acceptance"] = 1m,
        };

        SupervisorBudgetRecitation.Summary(10m, agentExecutionSpendUsd: 0m, brainPlaneSpendUsd: 2m, byKind)
            .ShouldBe("$2.00 spent of $10.00 cap ($8.00 remaining) — critic.review $1.00, grader.acceptance $1.00");
    }
}
