using System.Reflection;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Workflows;
using CodeSpace.Core.Services.Workflows.Reconciliation;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Dtos.Workflows;
using CodeSpace.Messages.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace CodeSpace.UnitTests.Workflows;

/// <summary>
/// The unwaited-child sweep's bound, on a database of its own: one tick cancels at most <see cref="StuckRunReconcilerService.BatchSize"/>
/// children, oldest first, and a child some live parent is waiting for takes no place in that batch. Kept off the shared
/// integration database, where any other test's leftover children compete for the same slots.
/// </summary>
[Trait("Category", "Unit")]
public class UnwaitedChildSweepBatchTests
{
    private static readonly Guid TeamId = Guid.NewGuid();

    [Fact]
    public async Task One_tick_cancels_at_most_a_batch_of_unwaited_children_oldest_first()
    {
        await using var db = new CodeSpaceDbContext(new DbContextOptionsBuilder<CodeSpaceDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var now = DateTimeOffset.UtcNow;
        var pastGrace = now - StuckRunReconcilerService.UnwaitedChildStagingGrace - TimeSpan.FromMinutes(1);

        var finishedParent = AddRun(db, WorkflowRunSourceTypes.Manual, null, WorkflowRunStatus.Failure, now - TimeSpan.FromHours(2));   // WorkflowEngine terminal
        var liveParent = AddRun(db, WorkflowRunSourceTypes.Manual, null, WorkflowRunStatus.Suspended, now - TimeSpan.FromHours(2));    // WorkflowEngine park
        var waited = AddRun(db, WorkflowRunSourceTypes.ChildWorkflow, liveParent, WorkflowRunStatus.Running, now - TimeSpan.FromHours(1));   // SubworkflowService.StageChildRunAsync — older than every orphan
        AddSubworkflowWait(db, liveParent, waited);   // WorkflowEngine.TryParkAsync

        var orphans = Enumerable.Range(1, StuckRunReconcilerService.BatchSize + 3).Select(i => AddRun(db, WorkflowRunSourceTypes.ChildWorkflow, finishedParent, WorkflowRunStatus.Suspended, pastGrace - TimeSpan.FromMinutes(i))).Reverse().ToList();   // SubworkflowService.StageChildRunAsync, oldest first
        await db.SaveChangesAsync();

        var cancels = CancelRecorder.Create();
        var reconciler = new StuckRunReconcilerService(db, null!, null!, null!, null!, cancels, NullLogger<StuckRunReconcilerService>.Instance);   // the sweep reads only the database and cancels through the workflow service

        var cancelled = await reconciler.CancelUnwaitedChildRunsAsync(CancellationToken.None);

        cancelled.ShouldBe(StuckRunReconcilerService.BatchSize, "one tick is bounded by the batch size");
        ((CancelRecorder)(object)cancels).Cancelled.ShouldBe(orphans.Take(StuckRunReconcilerService.BatchSize).ToList(), "oldest first, and the waited child takes no slot");
    }

    private static Guid AddRun(CodeSpaceDbContext db, string sourceType, Guid? parentRunId, WorkflowRunStatus status, DateTimeOffset createdDate)
    {
        var id = Guid.NewGuid();
        db.WorkflowRun.Add(new WorkflowRun { Id = id, TeamId = TeamId, RunRequestId = Guid.NewGuid(), SourceType = sourceType, ParentRunId = parentRunId, Status = status, CreatedDate = createdDate });
        return id;
    }

    private static void AddSubworkflowWait(CodeSpaceDbContext db, Guid parentRunId, Guid childRunId) =>
        db.WorkflowRunWait.Add(new WorkflowRunWait { Id = Guid.NewGuid(), RunId = parentRunId, NodeId = "sub", IterationKey = string.Empty, WaitKind = WorkflowWaitKinds.Subworkflow, Token = childRunId.ToString(), Status = WorkflowWaitStatuses.Pending, CreatedAt = DateTimeOffset.UtcNow });

    /// <summary>An <see cref="IWorkflowService"/> that records each cancel and reports it done; every other member throws.</summary>
    public class CancelRecorder : DispatchProxy
    {
        public List<Guid> Cancelled { get; } = new();

        public static IWorkflowService Create() => Create<IWorkflowService, CancelRecorder>();

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.Name != nameof(IWorkflowService.CancelRunAsync)) throw new NotSupportedException(targetMethod?.Name);

            Cancelled.Add((Guid)args![0]!);
            return Task.FromResult<CancelRunOutcome?>(new CancelRunOutcome { Cancelled = true, Status = WorkflowRunStatus.Cancelled, AgentRunsCancelled = 0 });
        }
    }
}
