using CodeSpace.Core.Services.Agents.Eval.Benchmark;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Enums;
using Shouldly;

namespace CodeSpace.UnitTests.Agents.Benchmark;

[Trait("Category", "Unit")]
public sealed class BenchmarkResultCostTests
{
    [Fact]
    public void Every_physical_attempt_is_summed_for_the_cell()
    {
        BenchmarkResultCost.Sum(new[] { Result(0.25m), Result(0.75m) }).ShouldBe(new BenchmarkResultCost.Total(1m, false));
    }

    [Fact]
    public void A_mixed_known_and_missing_attempt_is_indeterminate()
    {
        BenchmarkResultCost.Sum(new[] { Result(0.25m), Result(null) }).ShouldBe(new BenchmarkResultCost.Total(null, true));
    }

    [Fact]
    public void An_explicitly_indeterminate_attempt_cannot_be_repriced_from_an_adjacent_value()
    {
        BenchmarkResultCost.Sum(new[] { Result(0.25m, indeterminate: true) }).ShouldBe(new BenchmarkResultCost.Total(null, true));
    }

    [Fact]
    public void No_capped_cost_evidence_preserves_legacy_uncapped_null_semantics()
    {
        BenchmarkResultCost.Sum(Array.Empty<AgentRunResult?>()).ShouldBe(new BenchmarkResultCost.Total(null, false));
        BenchmarkResultCost.Sum(new[] { Result(null) }).ShouldBe(new BenchmarkResultCost.Total(null, false));
    }

    [Fact]
    public void A_capped_physical_attempt_without_settled_cost_is_indeterminate()
    {
        BenchmarkResultCost.Sum(new[] { Result(null) }, requireSettledCost: true).ShouldBe(new BenchmarkResultCost.Total(null, true));
    }

    [Fact]
    public void An_unrepresentable_total_fails_closed()
    {
        BenchmarkResultCost.Sum(new[] { Result(decimal.MaxValue), Result(decimal.MaxValue) }).ShouldBe(new BenchmarkResultCost.Total(null, true));
    }

    private static AgentRunResult Result(decimal? cumulativeCost, bool indeterminate = false) => new()
    {
        Status = AgentRunStatus.Succeeded, ExitReason = "completed", CumulativeCostUsd = cumulativeCost, CostIndeterminate = indeterminate,
    };
}
