using System.Text.Json;
using CodeSpace.Messages.Agents;

namespace CodeSpace.Core.Services.Supervisor;

/// <summary>
/// The PURE fail-closed bound evaluator (PR-E E5, Rule 18.1-adjacent — no DB, no state). Given the rehydrated
/// turn context (whose counters are LEDGER FACTS folded on rehydrate — so every bound is counted from the
/// durable ledger, survives replay, and can't be reset by re-entering the node) + the resolved
/// <see cref="SupervisorGoalPlan"/>, it returns the terminal reason to FORCE-STOP with, or null to proceed.
///
/// <para>Split into two gates the turn loop applies in order: <see cref="PreDecision"/> (the run can't even take
/// one more decision — depth / round budget / no-progress) and <see cref="PostDecision"/> (the decider's chosen
/// decision would breach a per-decision bound — spawn fan-out / total-spawn cap). Each force-STOP is fail-closed:
/// a distinct, persisted terminal reason + a clean run completion, NEVER a silent truncation.</para>
/// </summary>
public static class SupervisorBounds
{
    /// <summary>
    /// The bounds that stop the run BEFORE the decider is even asked, in deterministic precedence so a re-entry
    /// re-derives the same forced stop: depth (a recursive supervisor nested too deep — checked at turn 0), then
    /// the round budget, then the best-effort no-progress guard. Returns the terminal reason, or null to proceed.
    /// </summary>
    public static string? PreDecision(SupervisorTurnContext context, SupervisorGoalPlan plan, int supervisorDepth)
    {
        if (supervisorDepth >= SupervisorLane.MaxSupervisorDepth) return SupervisorStopReasons.DepthCapExceeded;

        if (context.TurnNumber >= plan.MaxRounds) return SupervisorStopReasons.BudgetExhausted;

        if (context.NoProgressDecisions >= plan.MaxNoProgressDecisions) return SupervisorStopReasons.NoProgress;

        return null;
    }

    /// <summary>
    /// The bounds that refuse the DECIDER'S CHOSEN decision: a spawn decision whose fan-out K exceeds the
    /// per-decision cap (<c>MaxParallelism</c>, ≤ the schema maxItems — a runtime guard against a schema-bypassing
    /// decider), or whose K would push the run's total spawned past <c>MaxTotalSpawns</c>. Only spawn/retry are
    /// bounded here (the other verbs create no agents). Returns the terminal reason, or null to proceed.
    /// </summary>
    public static string? PostDecision(SupervisorTurnContext context, SupervisorGoalPlan plan, SupervisorDecision decision)
    {
        if (!SupervisorGovernance.IsSideEffecting(decision.Kind)) return null;

        // Resolver loop (#379): a resolve attempt past the dedicated cap force-STOPs so a conflict that won't reconcile
        // falls back fail-safe to the humans. Counted from the ledger tape (replay-deterministic) — the CURRENT resolve
        // isn't on the tape yet, so cap=1 allows the first and refuses the second.
        if (decision.Kind == SupervisorDecisionKinds.Resolve
            && context.PriorDecisions.Count(d => d.DecisionKind == SupervisorDecisionKinds.Resolve) >= plan.MaxResolveAttempts)
            return SupervisorStopReasons.ResolveAttemptsExceeded;

        var k = SpawnCount(decision);

        if (k > plan.MaxParallelism) return SupervisorStopReasons.SpawnFanOutExceedsCap;

        if (context.TotalSpawnedAgents + k > plan.MaxTotalSpawns) return SupervisorStopReasons.TotalSpawnCapReached;

        // SOTA #4: realized-spend backpressure. STRICT > matches the total-spawn convention above (exactly-at-budget
        // still proceeds; spend that has ALREADY EXCEEDED the cap stops the next spend-incurring decision). Spend lands
        // only at agent completion, so this sees the PRIOR wave's realized cost — worst-case overshoot is one wave.
        if (plan.MaxCostUsd is { } cap && context.RunSpendUsd > cap) return SupervisorStopReasons.CostCapReached;

        return null;
    }

    /// <summary>How many agents the decision would spawn: a spawn fans out its <c>subtaskIds</c>; a retry is exactly one. Best-effort read — a malformed payload reads 0 (it stages nothing, so it can't breach a count bound).</summary>
    internal static int SpawnCount(SupervisorDecision decision)
    {
        if (decision.Kind is SupervisorDecisionKinds.Retry or SupervisorDecisionKinds.Resolve) return 1;

        if (decision.Kind != SupervisorDecisionKinds.Spawn) return 0;

        try
        {
            var root = JsonDocument.Parse(decision.PayloadJson).RootElement;

            return root.ValueKind == JsonValueKind.Object && root.TryGetProperty("subtaskIds", out var ids) && ids.ValueKind == JsonValueKind.Array
                ? ids.GetArrayLength()
                : 0;
        }
        catch (JsonException)
        {
            return 0;
        }
    }
}
