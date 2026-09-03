namespace CodeSpace.Messages.Agents;

/// <summary>
/// A4: the north-star OVER TIME — daily buckets plus the by-arm slices, read from the durable <c>run_scorecard</c>
/// rows only. This is the answer the live-computed scorecard could never give: it scores the most recent ≤100 runs
/// and throws the result away, so "the rate went from X to Y" was unanswerable and every CI real-model run was a
/// discarded point estimate.
///
/// <para>A window with no persisted rows comes back with no buckets and no slices — never a fabricated flat line.
/// <see cref="ScoredRuns"/> says exactly how many rows the answer was measured over.</para>
/// </summary>
public sealed record RunScorecardTrend
{
    /// <summary>The window's first day (inclusive), UTC date at midnight — the horizon the buckets cover, whether or not each day has runs.</summary>
    public required DateTimeOffset Since { get; init; }

    /// <summary>Persisted run rows the whole answer was measured over.</summary>
    public required int ScoredRuns { get; init; }

    /// <summary>One bucket per UTC day that actually HAS runs, oldest first. A day with no terminal runs is absent, never a zero-rate point (a zero rate and no data are different claims).</summary>
    public required IReadOnlyList<RunScorecardTrendBucket> Buckets { get; init; }

    /// <summary>The window sliced by lesson A/B arm — the same shape the live rollup carries, over the persisted rows.</summary>
    public required IReadOnlyList<LessonArmSlice> ByLessonArm { get; init; }
}

/// <summary>One UTC day of the trend: how many runs terminalized, what share of them hit the north-star, and what they cost.</summary>
public sealed record RunScorecardTrendBucket
{
    /// <summary>The bucket's UTC day at midnight.</summary>
    public required DateTimeOffset Day { get; init; }

    public required int Runs { get; init; }
    public required int SolvedRuns { get; init; }
    public required int DeliveredRuns { get; init; }
    public required int UnattendedSolvedWithDeliveryRuns { get; init; }

    /// <summary><see cref="UnattendedSolvedWithDeliveryRuns"/> / <see cref="Runs"/>, in 0..1.</summary>
    public required double UnattendedSolveWithDeliveryRate { get; init; }

    /// <summary>Summed agent-execution USD over the day's PRICED runs; null when none were priceable (never a silent $0).</summary>
    public decimal? CostUsd { get; init; }

    /// <summary>Summed brain-plane USD over the day's priced runs; null when none carried a priced brain call.</summary>
    public decimal? BrainPlaneUsd { get; init; }
}
