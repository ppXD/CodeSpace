using CodeSpace.Messages.Enums;

namespace CodeSpace.Messages.Agents;

/// <summary>
/// One agent run reduced to the fields a scorecard measures — the input the pure scorer aggregates. Decoupled
/// from the AgentRun entity so the scorer is testable without a DB and reusable by a future fixed-task eval bench.
/// </summary>
public sealed record AgentRunOutcome
{
    public required string Harness { get; init; }
    public required AgentRunStatus Status { get; init; }

    /// <summary>Wall-clock seconds the run took; null when it never started/completed (so it's excluded from latency stats).</summary>
    public double? DurationSeconds { get; init; }
}

/// <summary>Success + latency rollup for one harness (or, with <see cref="EvalScorecard.OverallLabel"/>, across all).</summary>
public sealed record HarnessScore
{
    public required string Harness { get; init; }

    /// <summary>Terminal runs scored (Succeeded / Failed / Cancelled / TimedOut). In-flight runs are not counted.</summary>
    public required int Total { get; init; }
    public required int Succeeded { get; init; }

    /// <summary>Succeeded / Total in 0..1; 0 when there are no terminal runs.</summary>
    public required double SuccessRate { get; init; }

    /// <summary>Median / 95th-percentile run duration over the runs that have one; null when none do.</summary>
    public double? P50DurationSeconds { get; init; }
    public double? P95DurationSeconds { get; init; }
}

/// <summary>A per-harness success/latency scorecard plus an overall rollup — the "is the agent actually working" view.</summary>
public sealed record AgentRunScorecard
{
    /// <summary>Per-harness scores, sorted by harness name.</summary>
    public required IReadOnlyList<HarnessScore> Harnesses { get; init; }

    /// <summary>The rollup across every harness (<see cref="HarnessScore.Harness"/> == <see cref="EvalScorecard.OverallLabel"/>).</summary>
    public required HarnessScore Overall { get; init; }
}
