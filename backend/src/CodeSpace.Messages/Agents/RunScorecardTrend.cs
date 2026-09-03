namespace CodeSpace.Messages.Agents;

/// <summary>
/// A4: the north-star OVER TIME — daily buckets plus the by-arm slices, read from the durable <c>run_scorecard</c>
/// rows. This is the answer the live-computed scorecard could never give: it scores the most recent ≤100 runs and
/// throws the result away, so "the rate went from X to Y" was unanswerable and every CI real-model run was a
/// discarded point estimate.
///
/// <para>A window with no activity comes back with no buckets and no slices — never a fabricated flat line.
/// <see cref="ScoredRuns"/> says exactly how many scored rows the answer was measured over.</para>
///
/// <para>WINDOWING DIFFERS from the live <c>UnattendedDeliveryScorecard</c>, deliberately. The live rollup windows
/// on a run's <c>CreatedDate</c> and caps at the most recent 100 runs; this trend windows on <c>CompletedAt</c> and
/// caps by days. A trend point belongs to the day a run FINISHED — that is when its outcome became true — whereas
/// the rollup answers "the last N runs I started." The two can therefore disagree at a window edge for a
/// long-running run, and neither is wrong.</para>
/// </summary>
public sealed record RunScorecardTrend
{
    /// <summary>The window's first day (inclusive), UTC date at midnight — the horizon the buckets cover, whether or not each day has runs.</summary>
    public required DateTimeOffset Since { get; init; }

    /// <summary>Persisted scored rows the rates were measured over (parked and pre-protocol runs are NOT in this count — they have their own per-bucket figures).</summary>
    public required int ScoredRuns { get; init; }

    /// <summary>One bucket per UTC day that saw ANY activity — a scored run, a park, or a pre-protocol run — oldest first. A day nothing happened on is absent, never a zero-rate point (a zero rate and no data are different claims).</summary>
    public required IReadOnlyList<RunScorecardTrendBucket> Buckets { get; init; }

    /// <summary>The window sliced by lesson A/B arm — the same shape the live rollup carries, over the persisted rows.</summary>
    public required IReadOnlyList<LessonArmSlice> ByLessonArm { get; init; }
}

/// <summary>One UTC day of the trend: how many runs terminalized, what share of them hit the north star, what they cost, and — beside the rate, never inside it — the two populations the rate cannot see.</summary>
public sealed record RunScorecardTrendBucket
{
    /// <summary>The bucket's UTC day at midnight.</summary>
    public required DateTimeOffset Day { get; init; }

    /// <summary>SCORED runs — terminal and contract-era. The denominator of <see cref="UnattendedSolveWithDeliveryRate"/>; zero on a day that only parked.</summary>
    public required int Runs { get; init; }

    public required int SolvedRuns { get; init; }
    public required int DeliveredRuns { get; init; }
    public required int UnattendedSolvedWithDeliveryRuns { get; init; }

    /// <summary>
    /// <see cref="UnattendedSolvedWithDeliveryRuns"/> / <see cref="Runs"/>, in 0..1 — NULL when <see cref="Runs"/>
    /// is zero, i.e. the day scored nothing. Nullable on purpose: a day that only parked would otherwise render 0%,
    /// which reads as "everything failed" when the truth is "nothing finished."
    /// </summary>
    public double? UnattendedSolveWithDeliveryRate { get; init; }

    /// <summary>
    /// Runs CREATED this day that are still suspended — the parked population the terminal denominator structurally
    /// cannot see. Without it a park-heavy day silently thins <see cref="Runs"/> and flatters the rate: the runs
    /// that went badly enough to stop and ask simply leave the measurement. Mirrors the live rollup's
    /// <c>SuspendedRuns</c>, per day. Windowed on CreatedDate (a park has no completion) — unlike every other
    /// figure on this bucket.
    /// </summary>
    public int SuspendedRuns { get; init; }

    /// <summary>Runs that terminalized this day PRE-PROTOCOL (no completion policy) — visible, never scored, mirroring the live rollup's <c>LegacyRuns</c>. Old tape is never re-derived into a trend point.</summary>
    public int LegacyRuns { get; init; }

    /// <summary>Summed agent-execution USD over the day's PRICED runs; null when none were priceable (never a silent $0).</summary>
    public decimal? CostUsd { get; init; }

    /// <summary>Summed brain-plane USD over the day's priced runs; null when none carried a priced brain call.</summary>
    public decimal? BrainPlaneUsd { get; init; }
}
