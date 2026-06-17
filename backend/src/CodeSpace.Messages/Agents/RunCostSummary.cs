namespace CodeSpace.Messages.Agents;

/// <summary>
/// The token + estimated-USD spend of ONE run, summed over its agent runs (SOTA #4). A pure read noun (Rule 18.1).
/// <see cref="EstimatedCostUsd"/> is null when NO counted run had a priceable model (all unknown) — distinct from
/// 0 (priced, but free); <see cref="UnknownCostRuns"/> counts the runs whose model/usage could not be priced
/// (fail-open), so the estimate is honestly qualified rather than silently undercounting.
/// </summary>
public sealed record RunCostSummary
{
    /// <summary>The run these agent runs belong to (AgentRun.WorkflowRunId).</summary>
    public required Guid WorkflowRunId { get; init; }

    /// <summary>Summed input (prompt) tokens across the run's terminal agent runs.</summary>
    public long SummedInputTokens { get; init; }

    /// <summary>Summed output (completion) tokens across the run's terminal agent runs.</summary>
    public long SummedOutputTokens { get; init; }

    /// <summary>Estimated USD cost of the priceable agent runs; null when none could be priced (every model unknown).</summary>
    public decimal? EstimatedCostUsd { get; init; }

    /// <summary>How many terminal agent runs were counted into this summary.</summary>
    public int CountedRuns { get; init; }

    /// <summary>How many of the counted runs could NOT be priced (no usage captured, or an unknown/blank model) — the fail-open honesty qualifier on the estimate.</summary>
    public int UnknownCostRuns { get; init; }
}

/// <summary>
/// A team's cumulative token + estimated-USD spend over a window, plus the per-run breakdown (SOTA #4). A pure read
/// noun (Rule 18.1). The summed totals always cover the FULL window (a sum needs no payload cap); the per-run
/// <see cref="Runs"/> list MAY be bounded for payload size, in which case <see cref="Truncated"/> is true and
/// <see cref="WindowRunCount"/> reports how many runs the window actually held.
/// </summary>
public sealed record TeamCostRollup
{
    /// <summary>Summed input tokens across all the team's runs in the window.</summary>
    public long TotalInputTokens { get; init; }

    /// <summary>Summed output tokens across all the team's runs in the window.</summary>
    public long TotalOutputTokens { get; init; }

    /// <summary>Estimated USD cost across the window; null when nothing in the window could be priced.</summary>
    public decimal? EstimatedCostUsd { get; init; }

    /// <summary>How many runs (distinct WorkflowRunId) are represented in <see cref="Runs"/>.</summary>
    public int RunCount { get; init; }

    /// <summary>How many counted agent runs across the window could not be priced (the fail-open qualifier).</summary>
    public int UnknownCostRuns { get; init; }

    /// <summary>How many runs the window actually held (≥ <see cref="RunCount"/> when <see cref="Truncated"/>).</summary>
    public int WindowRunCount { get; init; }

    /// <summary>True when <see cref="Runs"/> was bounded for payload size; the summed totals still cover the full window.</summary>
    public bool Truncated { get; init; }

    /// <summary>The per-run breakdown (most-recent first), possibly payload-bounded (see <see cref="Truncated"/>).</summary>
    public IReadOnlyList<RunCostSummary> Runs { get; init; } = Array.Empty<RunCostSummary>();
}
