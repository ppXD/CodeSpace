using CodeSpace.Messages.Dtos.Agents;

namespace CodeSpace.Core.Services.Supervisor;

/// <summary>
/// The turn loop's RESOLVED view of a supervisor node's <see cref="SupervisorGoalConfig"/> — the clamped /
/// defaulted limits + approval policy it actually enforces (PR-E E5). Mirrors <c>MapPlan</c> /
/// <c>LoopPlan</c>: a pure value with ALL normalisation in one place so the turn loop never trusts a raw
/// config field. Parsed leniently — a null / blank / out-of-range field falls back to the safe
/// <see cref="SupervisorLane"/> default, so a pre-E5 supervisor (goal only) resolves to exactly the historical
/// bounds (the default fan-out / total-spawn / no-progress caps, no approval gate).
///
/// <para>The bounds READ from this plan; the SupervisorLane consts are the fallbacks. There is NO round / decision-count
/// budget — a supervised run LOOPS UNTIL DONE, bounded by the total-spawn cap + the cost cap + the no-progress guard +
/// the model's own success stop.</para>
/// </summary>
public sealed record SupervisorGoalPlan
{
    /// <summary>Max agents one spawn decision may fan out — clamped to <c>[1, SpawnKCeiling]</c> (the schema's hard maxItems). A spawn beyond it force-STOPs ("spawn fan-out exceeds cap").</summary>
    public required int MaxParallelism { get; init; }

    /// <summary>Max agents the whole run may spawn in total (summed from the ledger) — clamped to <c>[1, MaxTotalSpawnsCeiling]</c>. At the cap a further spawn force-STOPs ("total spawn cap reached").</summary>
    public required int MaxTotalSpawns { get; init; }

    /// <summary>Consecutive no-new-result decisions before the best-effort no-progress guard force-STOPs ("no progress"). Clamped to <c>[1, NoProgressCeiling]</c>.</summary>
    public required int MaxNoProgressDecisions { get; init; }

    /// <summary>Max <c>resolve</c> attempts against a conflicted integration (resolver loop #379) before a further resolve force-STOPs ("resolve attempts exhausted") and the conflict falls back to the humans. Clamped to <c>[1, MaxResolveAttemptsCeiling]</c>.</summary>
    public required int MaxResolveAttempts { get; init; }

    /// <summary>Which decisions require a human approval before their side effect fires — maps to the autonomy tier <c>AgentToolGate</c> reads.</summary>
    public required SupervisorApprovalPolicy ApprovalPolicy { get; init; }

    /// <summary>The run's realized-spend cap in USD (SOTA #4), or null = no cost cap (the agent-count cap still bounds the run). When set, spend ABOVE it force-STOPs the next spend-incurring decision ("cost cap reached"). Unbounded above — an operator may set any positive ceiling (cost is realized after the fact, so a generous cap can't over-spawn beyond the count cap).</summary>
    public decimal? MaxCostUsd { get; init; }

    /// <summary>The hard ceiling on a single spawn decision's fan-out — the schema's <c>maxItems</c> on <c>spawn.subtaskIds</c> ([1,20]). The runtime guard pins to the SAME literal so a schema-bypassing decider can't fan out wider.</summary>
    public const int SpawnKCeiling = 20;

    /// <summary>Normalise the operator's config into a safe plan: every limit clamped, every absent field defaulted, the approval policy parsed leniently. A null config (no E5 fields authored) yields the all-defaults plan.</summary>
    public static SupervisorGoalPlan From(SupervisorGoalConfig? config) => new()
    {
        MaxParallelism = Clamp(config?.MaxParallelism, SpawnKCeiling, max: SpawnKCeiling),
        MaxTotalSpawns = Clamp(config?.MaxTotalSpawns, SupervisorLane.DefaultMaxTotalSpawns, max: SupervisorLane.MaxTotalSpawnsCeiling),
        MaxNoProgressDecisions = Clamp(config?.MaxNoProgressDecisions, SupervisorLane.DefaultMaxNoProgressDecisions, max: NoProgressCeiling),
        MaxResolveAttempts = Clamp(config?.MaxResolveAttempts, SupervisorLane.DefaultMaxResolveAttempts, max: SupervisorLane.MaxResolveAttemptsCeiling),
        ApprovalPolicy = ParseApprovalPolicy(config?.ApprovalPolicy),
        MaxCostUsd = NormalizeCost(config?.MaxCostUsd),
    };

    /// <summary>A cost cap is kept verbatim when positive; null / zero / negative resolves to null (no budget — never a zero budget that would block the first spawn before any spend is even known). Unlike the count caps there is no ceiling: realized spend can't run away because the agent-count cap bounds how many agents ever spawn.</summary>
    private static decimal? NormalizeCost(decimal? raw) => raw is { } value && value > 0m ? value : null;

    /// <summary>A no-progress cap ceiling so a fat-fingered config can't push it arbitrarily high — the hard limit on how many consecutive no-progress decisions a run may take before it force-stops (the runaway backstop now that there is no round budget).</summary>
    private const int NoProgressCeiling = SupervisorLane.DecisionBudget;

    /// <summary>Clamp an optional operator value into <c>[1, max]</c>; null / out-of-range falls back to <paramref name="default"/>. Mirrors AdmissionController.ParseCap.</summary>
    private static int Clamp(int? raw, int @default, int max) =>
        raw is { } value ? Math.Clamp(value, 1, max) : @default;

    /// <summary>Lenient parse — only the explicit gate values opt into approval; anything else (null, blank, typo) is the safe default None (no gate, pre-E5 behaviour).</summary>
    private static SupervisorApprovalPolicy ParseApprovalPolicy(string? raw) =>
        raw?.Trim().ToLowerInvariant() is "spawns" or "side-effects" or "side_effects"
            ? SupervisorApprovalPolicy.Spawns
            : SupervisorApprovalPolicy.None;
}
