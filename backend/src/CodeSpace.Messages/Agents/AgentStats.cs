using CodeSpace.Messages.Enums;

namespace CodeSpace.Messages.Agents;

/// <summary>
/// One agent run reduced to the fields the PER-AGENT stats scorer aggregates — the analogue of
/// <see cref="AgentRunOutcome"/> but grouped by the PERSONA that ran it rather than the harness, and carrying the
/// priced cost so a persona's spend rolls up in the same pass. Decoupled from the AgentRun entity so the scorer
/// stays DB-free and unit-testable.
///
/// <para>Cost is TWO fields on purpose (mirroring <c>TeamCostService</c>): <see cref="CostEligible"/> marks a run
/// that completed and persisted a result (so it participates in the spend accounting at all), and <see cref="Cost"/>
/// is that run's priced USD — null when the model/usage could not be priced. A non-eligible (in-flight) run is
/// excluded from the cost sum entirely, never counted as unknown-cost.</para>
/// </summary>
public sealed record AgentRunStatSample
{
    public required Guid AgentDefinitionId { get; init; }
    public required AgentRunStatus Status { get; init; }

    /// <summary>Wall-clock seconds the run took; null when it never started/completed (so it's excluded from latency stats).</summary>
    public double? DurationSeconds { get; init; }

    /// <summary>True when the run persisted a result (ResultJson present) — the row participates in the spend accounting. A false (in-flight) row is excluded from cost entirely, not counted as unknown.</summary>
    public required bool CostEligible { get; init; }

    /// <summary>The run's priced USD, or null when the model/usage was unpriceable. Meaningful only when <see cref="CostEligible"/>.</summary>
    public decimal? Cost { get; init; }

    /// <summary>When the run was created — drives both the "last active" stamp and the recent-outcomes ordering.</summary>
    public required DateTimeOffset CreatedAt { get; init; }
}

/// <summary>
/// Success + latency + spend rollup for ONE persona over a team's agent-run history — the per-agent analogue of
/// <see cref="HarnessScore"/>. This is the row the redesigned Agents roster reads to show each agent's recent-run
/// evidence (a sparkline of outcomes, a windowed success rate, latency, spend, and how recently it ran).
/// </summary>
public sealed record AgentStat
{
    public required Guid AgentDefinitionId { get; init; }

    /// <summary>Terminal runs scored in the window (every terminal status — including NeedsReview). In-flight (Queued / Running) runs are not counted, matching <see cref="HarnessScore.Total"/>.</summary>
    public required int Total { get; init; }
    public required int Succeeded { get; init; }

    /// <summary>Succeeded / Total in 0..1; 0 when there are no terminal runs.</summary>
    public required double SuccessRate { get; init; }

    /// <summary>Median / 95th-percentile run duration over the runs that have one; null when none do.</summary>
    public double? P50DurationSeconds { get; init; }
    public double? P95DurationSeconds { get; init; }

    /// <summary>The persona's summed spend over its cost-eligible runs, or null when NONE was priceable (distinct from a real $0).</summary>
    public decimal? EstimatedCostUsd { get; init; }

    /// <summary>Cost-eligible runs whose model/usage could not be priced — the honesty qualifier on <see cref="EstimatedCostUsd"/> (never silently undercounted).</summary>
    public required int UnknownCostRuns { get; init; }

    /// <summary>When the persona most recently ran (any status) — the "active N ago" stamp. Always present: a persona appears here only if it has at least one run.</summary>
    public required DateTimeOffset LastRunAt { get; init; }

    /// <summary>The persona's last N runs' statuses, oldest→newest (a sparkline the FE renders left-to-right). Includes in-flight runs, so a running one shows as a live dot the success rate doesn't count.</summary>
    public required IReadOnlyList<AgentRunStatus> RecentOutcomes { get; init; }
}

/// <summary>
/// Per-agent run stats across a team's personas — one <see cref="AgentStat"/> per persona that has at least one run
/// in the window. The redesigned Agents roster joins these onto its persona list by <see cref="AgentStat.AgentDefinitionId"/>;
/// a persona with no runs simply has no entry (its row renders an empty state). A wrapper (not a bare list) so the
/// window/coverage can grow forward-compatibly, mirroring <see cref="AgentRunScorecard"/>.
/// </summary>
public sealed record AgentStatsRollup
{
    /// <summary>One stat per persona with runs in the window, sorted by persona id (deterministic — the FE re-sorts for display).</summary>
    public required IReadOnlyList<AgentStat> Agents { get; init; }
}
