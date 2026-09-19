using System.Text.Json;
using CodeSpace.Core.Services.Workflows.Llm;
using CodeSpace.Messages.Agents;

namespace CodeSpace.Core.Services.Agents.Cost;

/// <summary>
/// Prices one completed coding-CLI attempt and folds it into its retry chain's monitored ceiling. Pure: callers load
/// the team's price table once and supply it. An uncapped task is returned by reference, while a capped task fails
/// honest with <see cref="AgentRunResult.CostIndeterminate"/> whenever complete accounting is impossible.
/// </summary>
public static class AgentRunBudget
{
    /// <summary>Stamp the known-zero accounting fact when validation rejects a run before any physical CLI invocation.</summary>
    public static AgentRunResult WithoutInvocation(AgentTask task, AgentRunResult result)
    {
        if (task.MaxCostUsd is null) return result;
        if (task.BudgetSpentUsd is < 0) return result with { CostUsd = null, CumulativeCostUsd = null, CostIndeterminate = true };
        return result with { CostUsd = 0m, CumulativeCostUsd = task.BudgetSpentUsd ?? 0m, CostIndeterminate = false };
    }

    /// <summary>
    /// What a terminal writer OUTSIDE the executor may settle this run's live budget claim at, read from the run's
    /// own persisted result. An operator cancel and the reconciler's abandon land a run terminal without ever
    /// holding its claim, so the durable row is the only spend evidence they have.
    ///
    /// <para>Null — which the ledger records as Indeterminate at the reserve — for every shape that is not a figure
    /// somebody was billed: no result at all (the ordinary case for a run killed mid-flight), an unparseable one, or
    /// one whose own accounting came back <see cref="AgentRunResult.CostIndeterminate"/>. It reads
    /// <see cref="AgentRunResult.CostUsd"/>, this attempt's own cost, because the claim it settles is per-invocation
    /// too; the chain's running total belongs to <see cref="AgentRunResult.CumulativeCostUsd"/> and would charge the
    /// live claim for rounds that settled at their own exits.</para>
    /// </summary>
    public static decimal? ObservedUsd(string? resultJson)
    {
        if (string.IsNullOrWhiteSpace(resultJson)) return null;

        try
        {
            var result = JsonSerializer.Deserialize<AgentRunResult>(resultJson, AgentJson.Options);

            return result is null || result.CostIndeterminate || result.CostUsd is not { } cost ? null : Math.Max(0m, cost);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static AgentRunResult Apply(AgentTask task, AgentRunResult result, IReadOnlyDictionary<string, ModelPrice> prices)
    {
        if (task.MaxCostUsd is null) return result;

        var model = result.Model ?? task.Model;
        var usage = result.TokenUsage;

        // WHICH rates this fold used, stamped on EVERY priced outcome including the indeterminate ones: a run whose
        // usage was incomplete was still valued against a specific table, and an auditor asking "what would this have
        // cost" needs the rates as much as a run that landed a number. Null only when no table prices the model,
        // which is also exactly when the cost below is unknowable.
        var snapshot = AgentCostPricing.SnapshotFor(model, prices);
        var cost = usage is null ? null : LlmUsageCost.Usd(model, new LlmUsage { InputTokens = usage.InputTokens, OutputTokens = usage.OutputTokens }, prices);

        if (cost is not { } actual || task.BudgetSpentUsd is < 0) return result with { CostUsd = null, CumulativeCostUsd = null, CostIndeterminate = true, PriceSnapshot = snapshot };

        try
        {
            return result with { CostUsd = actual, CumulativeCostUsd = (task.BudgetSpentUsd ?? 0m) + actual, CostIndeterminate = false, PriceSnapshot = snapshot };
        }
        catch (OverflowException)
        {
            return result with { CostUsd = null, CumulativeCostUsd = null, CostIndeterminate = true, PriceSnapshot = snapshot };
        }
    }
}
