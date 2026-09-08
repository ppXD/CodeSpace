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
