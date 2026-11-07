using CodeSpace.Core.Handlers.CommandHandlers.Workflows;
using CodeSpace.Core.Services.Workflows.Reconciliation;
using CodeSpace.Messages.Commands.Workflows;
using Shouldly;

namespace CodeSpace.UnitTests.Handlers.Workflows;

/// <summary>
/// Unit-proves <see cref="ReconcileStuckRunsCommandHandler"/> carries every sweep's count onto the response unchanged. Each
/// count gets a different value, so a field mapped from its neighbour, or left at its default, shows here and not as a
/// recurring-job log line that quietly under-reports one sweep.
/// </summary>
[Trait("Category", "Unit")]
public class ReconcileStuckRunsCommandHandlerTests
{
    [Fact]
    public async Task Every_sweep_count_reaches_the_response()
    {
        var summary = new StuckRunReconcileSummary
        {
            RedispatchedFromPending = 1, RevertedFromEnqueued = 2, MarkedAbandonedFromRunning = 3, RedispatchedFromStrandedSuspended = 4,
            RecoveredSupervisorAdvance = 5, RecoveredAbandonedSupervisorRun = 6, RecoveredStrandedTimerWait = 7,
            RecoveredStrandedSupervisorInfraParkWait = 8, RecoveredStrandedSubworkflowParent = 9, CancelledUnwaitedChildRun = 10,
        };

        var response = await new ReconcileStuckRunsCommandHandler(new FixedReconciler(summary)).Handle(new ReconcileStuckRunsCommand(), CancellationToken.None);

        response.RedispatchedFromPending.ShouldBe(1);
        response.RevertedFromEnqueued.ShouldBe(2);
        response.MarkedAbandonedFromRunning.ShouldBe(3);
        response.RedispatchedFromStrandedSuspended.ShouldBe(4);
        response.RecoveredSupervisorAdvance.ShouldBe(5);
        response.RecoveredAbandonedSupervisorRun.ShouldBe(6);
        response.RecoveredStrandedTimerWait.ShouldBe(7);
        response.RecoveredStrandedSupervisorInfraParkWait.ShouldBe(8);
        response.RecoveredStrandedSubworkflowParent.ShouldBe(9);
        response.CancelledUnwaitedChildRun.ShouldBe(10, "the children the tick cancelled because no parent was waiting for them");
    }

    private sealed class FixedReconciler : IStuckRunReconcilerService
    {
        private readonly StuckRunReconcileSummary _summary;

        public FixedReconciler(StuckRunReconcileSummary summary) { _summary = summary; }

        public Task<StuckRunReconcileSummary> ReconcileAsync(CancellationToken cancellationToken) => Task.FromResult(_summary);
    }
}
