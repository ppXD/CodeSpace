using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CodeSpace.Core.Services.Workflows.Reconciliation;

/// <summary>
/// Sub-workflow children no parent is waiting for: the sweep that cancels them, and the exclusion that keeps every wake sweep
/// off a child whose parent has finished in the meantime.
/// </summary>
public sealed partial class StuckRunReconcilerService
{
    /// <summary>
    /// How old a sub-workflow child must be before the sweep may judge it unwaited. The engine commits a staged child a moment
    /// before it parks the parent's <c>Subworkflow</c> wait naming it, so for that moment a wanted child has no wait. Matches
    /// <see cref="PendingStuckAfter"/>, so a child staged by a walk that died before parking is cancelled on the tick that
    /// would otherwise re-dispatch it. Committed, not configured.
    /// </summary>
    public static readonly TimeSpan UnwaitedChildStagingGrace = TimeSpan.FromMinutes(2);

    /// <summary>
    /// Cancel the sub-workflow children that no live run is waiting for — least recently attempted first, at most <see cref="BatchSize"/> a tick,
    /// each through the full <see cref="IWorkflowService.CancelRunAsync"/> so its own teardown kills its agents and cancels the
    /// children it waits for in turn. A child is left this way when its parent ends without reaching it — the engine's failure
    /// cleanup cancels only a child still Pending or Enqueued, a stop's cancel of a child can fail — or when its parent moves
    /// on without it: a Continue closes the ended attempt's waits and its re-walk stages a fresh child. Each cancel stands
    /// alone: a failure is logged and retried next tick, and the rest of the batch still goes. An attempt that fails or cancels
    /// nothing stamps the child's <c>LastModifiedDate</c>, so it queues behind the rest instead of holding a batch slot forever. A level below a cancelled child
    /// that its teardown did not reach is unwaited from then on, and a later tick takes it.
    /// </summary>
    internal async Task<int> CancelUnwaitedChildRunsAsync(CancellationToken cancellationToken)
    {
        try
        {
            var children = await FindUnwaitedChildRunsAsync(cancellationToken).ConfigureAwait(false);

            var cancelled = 0;

            foreach (var child in children)
                if (await TryCancelUnwaitedChildAsync(child, cancellationToken).ConfigureAwait(false)) cancelled++;

            return cancelled;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A failed find stops this sweep only; the sweeps before it have already run and the next tick tries again.
            _logger.LogWarning(ex, "StuckRunReconciler: the sweep for sub-workflow children no parent is waiting for failed; will retry next tick");
            return 0;
        }
    }

    /// <summary>
    /// The live sub-workflow children past <see cref="UnwaitedChildStagingGrace"/> that no live run holds a Pending
    /// <c>Subworkflow</c> wait for. The match is sound because only <c>SubworkflowService.StageChildRunAsync</c> writes this
    /// source type, and only the engine's park writes a <c>Subworkflow</c> wait, its token the id of the child it just staged —
    /// so every child some live run will resume is named by such a wait. A rerun or replay fork carries its own source type
    /// and is never a candidate; a child a live fork waits for is named by the fork's wait like any other.
    /// </summary>
    private async Task<List<UnwaitedChild>> FindUnwaitedChildRunsAsync(CancellationToken cancellationToken)
    {
        var threshold = DateTimeOffset.UtcNow - UnwaitedChildStagingGrace;

        return await _db.WorkflowRun.AsNoTracking()
            .Where(r => r.SourceType == WorkflowRunSourceTypes.ChildWorkflow && NonTerminalRunStatuses.Contains(r.Status) && r.CreatedDate < threshold)
            .Where(r => !_db.WorkflowRunWait.Any(w => w.Token == r.Id.ToString() && w.WaitKind == WorkflowWaitKinds.Subworkflow && w.Status == WorkflowWaitStatuses.Pending && _db.WorkflowRun.Any(p => p.Id == w.RunId && NonTerminalRunStatuses.Contains(p.Status))))
            .OrderBy(r => r.LastModifiedDate).ThenBy(r => r.CreatedDate)
            .Take(BatchSize)
            .Select(r => new UnwaitedChild(r.Id, r.TeamId))
            .ToListAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<bool> TryCancelUnwaitedChildAsync(UnwaitedChild child, CancellationToken cancellationToken)
    {
        try
        {
            var outcome = await _workflowService.CancelRunAsync(child.Id, child.TeamId, cancellationToken).ConfigureAwait(false);

            if (outcome?.Cancelled == true) return true;

            await RotateToBackAsync(child.Id, cancellationToken).ConfigureAwait(false);
            return false;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Per-row resilience: one child's failed cancel doesn't abort the sweep.
            _logger.LogWarning(ex, "StuckRunReconciler: cancelling sub-workflow child run {RunId}, which no parent is waiting for, failed; will retry next tick", child.Id);
            await RotateToBackAsync(child.Id, cancellationToken).ConfigureAwait(false);
            return false;
        }
    }

    /// <summary>Stamps a live child's <c>LastModifiedDate</c> (<c>ExecuteUpdateAsync</c> skips the audit hook) so the oldest-attempted-first batch moves past it. Best effort.</summary>
    private async Task RotateToBackAsync(Guid runId, CancellationToken cancellationToken)
    {
        try
        {
            var now = DateTimeOffset.UtcNow;

            await _db.WorkflowRun.Where(r => r.Id == runId && NonTerminalRunStatuses.Contains(r.Status)).ExecuteUpdateAsync(s => s.SetProperty(r => r.LastModifiedDate, now), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "StuckRunReconciler: moving sub-workflow child run {RunId} to the back of the unwaited-child batch failed", runId);
        }
    }

    /// <summary>
    /// Every run a wake or re-dispatch sweep may revive: all but a sub-workflow child whose parent has finished. No one waits for
    /// such a child — its parent's waits closed when it ended — so reviving it would spend on work nothing will read;
    /// <see cref="CancelUnwaitedChildRunsAsync"/> cancels it instead, and until that lands the dispatcher refuses it. The guard is
    /// by source type because <c>ParentRunId</c> is also a rerun's or replay's lineage, finished by definition.
    /// </summary>
    private IQueryable<WorkflowRun> RunsWithNoFinishedParent => _db.WorkflowRun.Where(r => !(r.SourceType == WorkflowRunSourceTypes.ChildWorkflow && _db.WorkflowRun.Any(p => p.Id == r.ParentRunId && TerminalRunStatuses.Contains(p.Status))));

    private static WorkflowRunStatus[] NonTerminalRunStatuses => WorkflowRunState.NonTerminal;

    private sealed record UnwaitedChild(Guid Id, Guid TeamId);
}
