using CodeSpace.Core.Services.Agents.Cost;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Enums;
using Shouldly;

namespace CodeSpace.UnitTests.Agents;

[Trait("Category", "Unit")]
public sealed class AgentRunBudgetTests
{
    [Fact]
    public void Apply_is_reference_identical_when_the_task_has_no_cap()
    {
        var result = new AgentRunResult { Status = AgentRunStatus.Succeeded, ExitReason = "completed", Model = "unknown" };

        AgentRunBudget.Apply(new AgentTask { Goal = "g", Harness = "h" }, result, ModelPriceResolver.Empty).ShouldBeSameAs(result);
    }

    [Fact]
    public void Apply_prices_observed_usage_and_accumulates_prior_attempts()
    {
        var task = new AgentTask { Goal = "g", Harness = "h", MaxCostUsd = 1m, BudgetSpentUsd = 0.25m };
        var result = new AgentRunResult { Status = AgentRunStatus.Succeeded, ExitReason = "completed", Model = "priced", TokenUsage = new AgentTokenUsage { InputTokens = 100_000, OutputTokens = 10_000 } };
        var prices = new Dictionary<string, ModelPrice> { ["priced"] = new() { InputPerMillionUsd = 2m, OutputPerMillionUsd = 10m } };

        var accounted = AgentRunBudget.Apply(task, result, prices);

        accounted.CostUsd.ShouldBe(0.3m);
        accounted.CumulativeCostUsd.ShouldBe(0.55m);
        accounted.CostIndeterminate.ShouldBeFalse();
    }

    [Fact]
    public void Apply_marks_capped_usage_indeterminate_when_the_observed_model_has_no_price()
    {
        var task = new AgentTask { Goal = "g", Harness = "h", MaxCostUsd = 1m };
        var result = new AgentRunResult { Status = AgentRunStatus.Succeeded, ExitReason = "completed", Model = "unknown", TokenUsage = new AgentTokenUsage { InputTokens = 1, OutputTokens = 1 } };

        var accounted = AgentRunBudget.Apply(task, result, ModelPriceResolver.Empty);

        accounted.CostUsd.ShouldBeNull();
        accounted.CumulativeCostUsd.ShouldBeNull();
        accounted.CostIndeterminate.ShouldBeTrue();
    }

    [Fact]
    public void Apply_uses_the_observed_model_instead_of_the_authored_fallback()
    {
        var task = new AgentTask { Goal = "g", Harness = "h", Model = "authored", MaxCostUsd = 10m };
        var result = new AgentRunResult { Status = AgentRunStatus.Succeeded, ExitReason = "completed", Model = "observed", TokenUsage = new AgentTokenUsage { InputTokens = 1_000_000, OutputTokens = 0 } };
        var prices = new Dictionary<string, ModelPrice>
        {
            ["authored"] = new() { InputPerMillionUsd = 1m, OutputPerMillionUsd = 1m },
            ["observed"] = new() { InputPerMillionUsd = 4m, OutputPerMillionUsd = 4m },
        };

        AgentRunBudget.Apply(task, result, prices).CostUsd.ShouldBe(4m);
    }

    [Fact]
    public void Apply_marks_missing_usage_indeterminate_instead_of_free()
    {
        var task = new AgentTask { Goal = "g", Harness = "h", Model = "priced", MaxCostUsd = 1m };
        var result = new AgentRunResult { Status = AgentRunStatus.Failed, ExitReason = "non-zero-exit" };

        AgentRunBudget.Apply(task, result, new Dictionary<string, ModelPrice> { ["priced"] = new() { InputPerMillionUsd = 1m, OutputPerMillionUsd = 1m } }).CostIndeterminate.ShouldBeTrue();
    }

    [Fact]
    public void WithoutInvocation_preserves_prior_spend_and_records_a_known_zero_attempt()
    {
        var task = new AgentTask { Goal = "g", Harness = "h", MaxCostUsd = 1m, BudgetSpentUsd = 0.25m };
        var result = new AgentRunResult { Status = AgentRunStatus.Failed, ExitReason = "acceptance-unavailable" };

        var accounted = AgentRunBudget.WithoutInvocation(task, result);

        accounted.CostUsd.ShouldBe(0m);
        accounted.CumulativeCostUsd.ShouldBe(0.25m);
        accounted.CostIndeterminate.ShouldBeFalse();
    }

    [Fact]
    public void Apply_stamps_the_snapshot_it_priced_with()
    {
        // MUTATION THIS CATCHES: dropping `PriceSnapshot = snapshot` from Apply's priced return. CostUsd would still
        // be 0.3 and every existing assertion above would stay green, while the number became unauditable — the rate
        // table has no effective-from column, so an operator editing "priced" to 4/20 tomorrow silently re-values
        // this run's $0.30 and nothing recorded what produced it.
        var task = new AgentTask { Goal = "g", Harness = "h", MaxCostUsd = 1m };
        var result = new AgentRunResult { Status = AgentRunStatus.Succeeded, ExitReason = "completed", Model = "priced", TokenUsage = new AgentTokenUsage { InputTokens = 100_000, OutputTokens = 10_000 } };
        var prices = new Dictionary<string, ModelPrice> { ["priced"] = new() { InputPerMillionUsd = 2m, OutputPerMillionUsd = 10m } };

        var snapshot = AgentRunBudget.Apply(task, result, prices).PriceSnapshot.ShouldNotBeNull();

        snapshot.Source.ShouldBe(ModelPriceSources.CredentialRow, "the operator's own per-model row priced this run, not the built-in table — an auditor must be able to tell which");
        snapshot.InputUsdPerMillion.ShouldBe(2m);
        snapshot.OutputUsdPerMillion.ShouldBe(10m);
        snapshot.Digest.ShouldBe(ModelPriceSnapshot.Of(ModelPriceSources.CredentialRow, 2m, 10m).Digest);
    }

    [Fact]
    public void Apply_stamps_the_rates_even_when_the_usage_could_not_be_priced()
    {
        // A capped run whose CLI reported no usage was still valued against a specific table. MUTATION: stamping the
        // snapshot only on the success return — the indeterminate row would then lose the rates that WOULD have
        // applied, which is exactly the evidence an "why was this indeterminate" audit needs.
        var task = new AgentTask { Goal = "g", Harness = "h", Model = "priced", MaxCostUsd = 1m };
        var result = new AgentRunResult { Status = AgentRunStatus.Failed, ExitReason = "non-zero-exit" };
        var prices = new Dictionary<string, ModelPrice> { ["priced"] = new() { InputPerMillionUsd = 1m, OutputPerMillionUsd = 3m } };

        var accounted = AgentRunBudget.Apply(task, result, prices);

        accounted.CostIndeterminate.ShouldBeTrue();
        accounted.PriceSnapshot.ShouldNotBeNull().InputUsdPerMillion.ShouldBe(1m);
    }

    [Fact]
    public void Apply_stamps_no_snapshot_when_no_table_prices_the_model()
    {
        // The honest absence: null snapshot means "nothing priced this", never "priced at zero".
        var task = new AgentTask { Goal = "g", Harness = "h", MaxCostUsd = 1m };
        var result = new AgentRunResult { Status = AgentRunStatus.Succeeded, ExitReason = "completed", Model = "no-such-model", TokenUsage = new AgentTokenUsage { InputTokens = 1, OutputTokens = 1 } };

        AgentRunBudget.Apply(task, result, ModelPriceResolver.Empty).PriceSnapshot.ShouldBeNull();
    }

    [Fact]
    public void The_price_snapshot_round_trips_through_AgentJson_so_the_stamp_is_durable()
    {
        // The stamp is only worth anything if it SURVIVES result_json — the envelope it is persisted through.
        var result = new AgentRunResult
        {
            Status = AgentRunStatus.Succeeded, ExitReason = "completed",
            PriceSnapshot = ModelPriceSnapshot.Of(ModelPriceSources.EnvOverride, 2.5m, 10m),
        };

        var rehydrated = System.Text.Json.JsonSerializer.Deserialize<AgentRunResult>(System.Text.Json.JsonSerializer.Serialize(result, AgentJson.Options), AgentJson.Options)!;

        rehydrated.PriceSnapshot.ShouldBe(result.PriceSnapshot);
    }

    [Theory]
    [InlineData(5.0, 5.0, 5.0)]     // the quick lane: node cap and run cap are the SAME projected number
    [InlineData(0.5, 5.0, 0.5)]     // a tighter task ceiling is the honest estimate
    [InlineData(100.0, 12.0, 12.0)] // a task ceiling ABOVE the run cap is unreachable — claiming it would refuse the first launch outright
    [InlineData(null, 5.0, 5.0)]    // no task ceiling: an opaque CLI's honest maximum IS the run cap
    [InlineData(100.0, null, 0.0)]  // no run cap: the unbudgeted row gates nothing, and the Room reads its reserve as spend
    [InlineData(null, null, 0.0)]
    public void The_pre_launch_estimate_is_clamped_to_the_cap_it_is_admitted_against(double? taskCap, double? runCap, double expected)
    {
        // MUTATION THIS CATCHES: `task.MaxCostUsd ?? cap` without the clamp. Row 3 then claims $100 against a $12 run
        // cap, so ReserveAsync refuses a launch on a run that has committed NOTHING — a false refusal over money the
        // CLI could never have spent, and the hardest kind to diagnose because the cap it names is not the one it read.
        var task = new AgentTask { Goal = "g", Harness = "h", MaxCostUsd = (decimal?)taskCap };

        AgentRunExecutor.RunSpendEstimate(task, (decimal?)runCap).ShouldBe((decimal)expected);
    }

    [Theory]
    [InlineData(true, 0.99, false)]
    [InlineData(false, 1.00, true)]
    [InlineData(false, 1.01, true)]
    public void Further_calls_stop_on_indeterminate_or_at_the_observed_ceiling(bool indeterminate, double cumulative, bool expectedFromSpend)
    {
        var task = new AgentTask { Goal = "g", Harness = "h", MaxCostUsd = 1m };
        var result = new AgentRunResult { Status = AgentRunStatus.Failed, ExitReason = "failed", CostIndeterminate = indeterminate, CumulativeCostUsd = (decimal)cumulative };

        AgentRunExecutor.CostBudgetStopsFurtherCalls(task, result).ShouldBe(indeterminate || expectedFromSpend);
    }
}
