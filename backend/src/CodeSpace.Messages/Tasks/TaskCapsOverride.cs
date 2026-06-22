namespace CodeSpace.Messages.Tasks;

/// <summary>
/// The operator's optional SAFETY-BUDGET caps for a launched task (Rule 18.1, a pure data noun) — the numeric
/// bounds that ride the router's <c>CapsOverride</c> seam and TIGHTEN the effort preset's caps. Every field is
/// OPTIONAL: a set value replaces the preset's; an unset (null) field keeps the preset default, so an absent
/// override is byte-identical to today's preset-only behaviour.
///
/// <para><b>Where each cap BINDS</b> — these are multi-agent / loop bounds, so they take effect on the projections
/// that actually loop or fan out: <see cref="MaxCostUsd"/> is enforced by the SUPERVISOR lane (the deep tier) — a
/// run whose realized spend exceeds it is force-stopped by <c>SupervisorBounds</c> (the named "cost cap before
/// public Deep" gate); <see cref="MaxParallelism"/> binds a fan-out (map / supervisor) projection. A single-agent
/// (quick) run has no spend-loop or fan-out to bound, so a cap set on it is inert by design — the caps reach
/// <c>RoutePlan.Caps</c> on every tier, but only a looping/fan-out projection consumes them.</para>
///
/// <para>Kept SEPARATE from <c>TaskExecutionOverrides</c> (the agent-profile harness/model/persona overrides) by
/// design: caps are a supervisor/cost concern that flows through the router's caps merge, not the agent envelope.
/// Autonomy + approval are NOT here — they have their own surfaces (the autonomy dial / approval policy); the
/// router's merge keeps those tighten-only regardless.</para>
/// </summary>
public sealed record TaskCapsOverride
{
    /// <summary>Max spend (USD) the run is allowed before the SUPERVISOR (deep tier) force-stops it; inert on a single-agent run (no spend-loop). Null = the preset's cap (or none). Must be POSITIVE when set.</summary>
    public decimal? MaxCostUsd { get; init; }

    /// <summary>Max branches a fan-out (map / supervisor) projection may run at once; inert on a single-agent run. Null = the preset default. Must be >= 1 when set.</summary>
    public int? MaxParallelism { get; init; }

    /// <summary>Max rounds / turns a looping projection may take. Null = the preset default. Must be >= 1 when set.</summary>
    public int? MaxRounds { get; init; }

    /// <summary>Max agents the run may spawn in total. Null = the preset default. Must be >= 1 when set.</summary>
    public int? MaxTotalSpawns { get; init; }

    /// <summary>True when no cap is set — the launch service then leaves the router's CapsOverride null (byte-identical to the preset-only path).</summary>
    public bool IsEmpty => MaxCostUsd is null && MaxParallelism is null && MaxRounds is null && MaxTotalSpawns is null;

    /// <summary>
    /// Fail-LOUD boundary validation: a set cap must be in a meaningful range. Without this a fat-fingered
    /// <c>MaxCostUsd = -50</c> (or 0) would silently degrade to "no cap" downstream (<c>SupervisorGoalPlan</c>
    /// nulls a non-positive cost), so the operator believes they capped spend but didn't. Throws
    /// <see cref="ArgumentException"/> on an invalid cap; a null cap (unset) is always valid.
    /// </summary>
    public void Validate()
    {
        if (MaxCostUsd is { } cost && cost <= 0) throw new ArgumentException($"MaxCostUsd must be positive when set (was {cost}).");
        if (MaxParallelism is { } p && p < 1) throw new ArgumentException($"MaxParallelism must be >= 1 when set (was {p}).");
        if (MaxRounds is { } r && r < 1) throw new ArgumentException($"MaxRounds must be >= 1 when set (was {r}).");
        if (MaxTotalSpawns is { } s && s < 1) throw new ArgumentException($"MaxTotalSpawns must be >= 1 when set (was {s}).");
    }

    /// <summary>Project onto the router's <see cref="RouteCaps"/> seam — ONLY the numeric caps; autonomy/approval/extra are left at their defaults so the router's tighten-only merge keeps the preset's values for them.</summary>
    public RouteCaps ToRouteCaps() => new()
    {
        MaxCostUsd = MaxCostUsd,
        MaxParallelism = MaxParallelism,
        MaxRounds = MaxRounds,
        MaxTotalSpawns = MaxTotalSpawns,
    };
}
