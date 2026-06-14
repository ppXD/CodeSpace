using CodeSpace.Core.Services.Workflows.Reconciliation;
using Shouldly;

namespace CodeSpace.UnitTests.Workflows;

/// <summary>
/// Pure pins for the stuck-run reconciler's public surface that has no DB dependency: the
/// stranded-Suspended grace threshold + the summary's <see cref="StuckRunReconcileSummary.Total"/>
/// rollup. The recovery behaviour itself is proved against real Postgres in the integration tier
/// (<c>StuckRunReconcilerFlowTests</c>); these are the fast, no-infra invariants.
/// </summary>
[Trait("Category", "Unit")]
public class StuckRunReconcilerSummaryTests
{
    [Fact]
    public void SuspendedStrandedAfter_is_two_minutes()
    {
        // Pinned: the grace window for the stranded-Suspended sweep. It only needs to clear the
        // sub-second resolve-then-flip window of a NORMAL last-wait resume (the predicate's
        // zero-pending-waits clause excludes every legitimately-parked run regardless of age).
        // Loosening it delays recovery of a stranded run; tightening it risks sweeping a run that
        // is mid-resume. 2 minutes is the chosen safe value — pin it so a change is deliberate.
        StuckRunReconcilerService.SuspendedStrandedAfter.ShouldBe(TimeSpan.FromMinutes(2));
    }

    [Fact]
    public void Total_includes_the_stranded_suspended_count()
    {
        // The 4th sweep's count MUST roll into Total — otherwise the recurring-job log line +
        // any "did the reconciler do anything" check silently under-reports stranded recoveries.
        var summary = new StuckRunReconcileSummary
        {
            RedispatchedFromPending = 1,
            RevertedFromEnqueued = 2,
            MarkedAbandonedFromRunning = 3,
            RedispatchedFromStrandedSuspended = 4,
        };

        summary.Total.ShouldBe(10, "Total must sum all four sweep counts, including the stranded-Suspended one");
    }

    [Fact]
    public void Total_counts_only_the_stranded_field_when_it_is_the_only_nonzero()
    {
        // Guards specifically against the new field being dropped from the Total sum.
        var summary = new StuckRunReconcileSummary { RedispatchedFromStrandedSuspended = 7 };

        summary.Total.ShouldBe(7);
    }
}
