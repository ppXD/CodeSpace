using CodeSpace.Core.Services.Supervisor;
using CodeSpace.Core.Services.Workflows.Reconciliation;
using CodeSpace.Messages.Agents;
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
    public void Total_includes_every_sweep_count()
    {
        // Every sweep's count MUST roll into Total — otherwise the recurring-job log line +
        // any "did the reconciler do anything" check silently under-reports recoveries. This
        // covers all six fields, incl. the supervisor self-advance + abandoned-supervisor-run ones.
        var summary = new StuckRunReconcileSummary
        {
            RedispatchedFromPending = 1,
            RevertedFromEnqueued = 2,
            MarkedAbandonedFromRunning = 3,
            RedispatchedFromStrandedSuspended = 4,
            RecoveredSupervisorAdvance = 5,
            RecoveredAbandonedSupervisorRun = 6,
        };

        summary.Total.ShouldBe(21, "Total must sum ALL sweep counts, including the abandoned-supervisor-run recovery");
    }

    [Fact]
    public void Total_counts_only_the_abandoned_supervisor_run_field_when_it_is_the_only_nonzero()
    {
        // Guards specifically against the new PR-E P1-2 field being dropped from the Total sum.
        var summary = new StuckRunReconcileSummary { RecoveredAbandonedSupervisorRun = 7 };

        summary.Total.ShouldBe(7);
    }

    [Fact]
    public void MaxSupervisorRunRecoveries_is_three()
    {
        // THE LOOP-GUARD cap (PR-E P1-2). Pinned because it is load-bearing: a deterministically
        // crashing supervisor run is re-dispatched at most this many times (counted from durable
        // supervisor.run_recovered ledger records) before it falls through to the abandoned-Running
        // failure sweep + terminates. Raising it lets a deterministic crash loop longer; lowering it
        // risks failing a run that a couple more transient retries would have recovered. 3 is chosen.
        StuckRunReconcilerService.MaxSupervisorRunRecoveries.ShouldBe(3);
    }
}
