namespace CodeSpace.Messages.Agents;

/// <summary>
/// P4 — the CLOSED, cross-lane bucket every run (single-agent, plan-map, supervisor alike) resolves to for the
/// premature-stop-rate metric. Deliberately coarser than <c>SupervisorStopKind</c> (which only applies to the
/// supervisor lane): this is the shared axis a run of ANY projection kind can be judged on. <see cref="StillInProgress"/>
/// is the bucket that makes the metric HONEST — a run that never reaches a terminal state is not silently dropped
/// from the population (the way a narrower "success rate over terminal runs" metric would), it is counted and
/// reported explicitly.
/// </summary>
public enum RunOutcomeBucket
{
    /// <summary>A genuine, clean conclusion — the model/harness finished on its own terms with no forced/degraded stop.</summary>
    Succeeded,

    /// <summary>The run did NOT finish cleanly — a forced bound/governance stop, a model give-up, a raw Failed/TimedOut/NeedsReview terminal status. The premature-stop-rate's numerator.</summary>
    Degraded,

    /// <summary>The OPERATOR deliberately cancelled the run — not a stability failure, excluded from the rate (neither numerator nor denominator).</summary>
    Cancelled,

    /// <summary>The run has not yet reached any terminal state (Pending/Enqueued/Running/Suspended) — reported separately, never silently excluded nor folded into either the numerator or the settled denominator.</summary>
    StillInProgress,
}

/// <summary>
/// P4 — the premature-stop-rate report: "of the runs we started, what fraction died/got stuck rather than reaching
/// a genuine conclusion" — the stability north-star this arc's own audit called for, DELIBERATELY denominated over
/// the FULL run population (not just the ones that happened to finish), so a systemic "runs never conclude" problem
/// shows up as a bad number instead of vanishing from a narrower metric's denominator.
/// </summary>
public sealed record PrematureStopRateReport
{
    /// <summary>Every qualifying run in the window, regardless of its current status.</summary>
    public int TotalRuns { get; init; }

    public int SucceededRuns { get; init; }
    public int DegradedRuns { get; init; }
    public int CancelledRuns { get; init; }
    public int StillInProgressRuns { get; init; }

    /// <summary>The subset of <see cref="StillInProgressRuns"/> that has been active longer than the stuck threshold (<c>PrematureStopRateService.StuckThresholdHoursEnvVar</c>) — surfaced LOUDLY rather than left invisible inside "still in progress".</summary>
    public int StuckRuns { get; init; }

    /// <summary>Succeeded + Degraded + Cancelled — INFORMATIONAL: how many runs reached SOME conclusion (of any kind) vs. are still in flight. NOT the rate's own denominator (see <see cref="PrematureStopRate"/>) — a deliberate operator Cancel settles the run but is not a stability outcome.</summary>
    public int SettledRuns => SucceededRuns + DegradedRuns + CancelledRuns;

    /// <summary>Degraded / (Succeeded + Degraded) — the north-star figure. A deliberate operator Cancel is excluded from BOTH halves: it measures user behavior, not system stability, and folding it in would dilute the rate with noise unrelated to "did the system keep the run alive." Null when there is nothing settled yet to divide over (never a misleading 0%).</summary>
    public double? PrematureStopRate => SucceededRuns + DegradedRuns == 0 ? null : (double)DegradedRuns / (SucceededRuns + DegradedRuns);
}
