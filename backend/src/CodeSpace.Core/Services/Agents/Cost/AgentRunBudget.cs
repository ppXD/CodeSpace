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
