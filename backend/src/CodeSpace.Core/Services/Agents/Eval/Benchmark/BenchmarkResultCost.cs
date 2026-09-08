using CodeSpace.Messages.Agents;

namespace CodeSpace.Core.Services.Agents.Eval.Benchmark;

/// <summary>Conservative cost fold for one benchmark cell. Every physical attempt must carry settled capped spend; an unknown attempt makes the whole cell indeterminate.</summary>
public static class BenchmarkResultCost
{
    public readonly record struct Total(decimal? CostUsd, bool Indeterminate);

    public static Total Sum(IEnumerable<AgentRunResult?> attempts, bool requireSettledCost = false)
    {
        var values = attempts.ToList();
        if (values.Any(value => value?.CostIndeterminate == true)) return new Total(null, true);
        var priced = values.Where(value => value?.CumulativeCostUsd is not null).ToList();
        if (priced.Count == 0) return new Total(null, requireSettledCost && values.Count > 0);
        if (priced.Count != values.Count) return new Total(null, true);

        try { return new Total(priced.Sum(value => value!.CumulativeCostUsd!.Value), false); }
        catch (OverflowException) { return new Total(null, true); }
    }
}
