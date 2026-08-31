using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Messages.Contracts;
using Microsoft.Extensions.Logging;

namespace CodeSpace.Core.Services.Agents.Capture;

/// <summary>
/// The plane as the producer of the <see cref="WorkflowRunDataOwnerKinds.HarnessExecution"/> completeness facet.
/// A new generation owes exactly one stable execution-identity row; a revise process reuses the live generation and
/// owes none. The K=1 expectation is stated before the row's transaction and its presence only after commit, while a
/// standalone Agent Run states no FACET because there is no Workflow Run to own the manifest. Its GAP is not the same
/// case since 0184: a refused generation of a standalone run is exactly as locatable a span, and it is recorded
/// against the Agent Run that owns it rather than swallowed for want of a parent.
///
/// <para>A refused new generation is split by its launch fence. While the worker still owns that fence, the minted
/// identity is a real known-missing row and becomes one bounded gap. After supersession, this plane cannot establish
/// that the generation was ever owed, so it unstates the expectation instead of manufacturing a missing identity.</para>
///
/// <para>This is observation-only shadow accounting. Every manifest/gap write is contained by
/// <c>IRunDataCompletenessWriter</c>; nothing here changes the Agent Run outcome or is read by completion, planner,
/// terminal authority, oracle, critic, harness selection, or model routing.</para>
/// </summary>
public sealed partial class NativeRecordPlane
{
    private async Task<bool> DeclareExecutionAsync(Guid teamId, Guid? workflowRunId, CancellationToken cancellationToken)
    {
        if (ExecutionAdvance(teamId, workflowRunId, expected: 1, present: 0) is not { } declaration) return false;

        return await _completeness.AdvanceAsync(declaration, cancellationToken).ConfigureAwait(false);
    }

    private async Task AccountForExecutionAsync(Guid teamId, Guid? workflowRunId, CancellationToken cancellationToken)
    {
        if (ExecutionAdvance(teamId, workflowRunId, expected: 0, present: 1) is not { } landed) return;

        await _completeness.AdvanceAsync(landed, cancellationToken).ConfigureAwait(false);
    }

    private static RunDataFacetAdvance? ExecutionAdvance(Guid teamId, Guid? workflowRunId, long expected, long present) =>
        workflowRunId is { } runId
            ? new RunDataFacetAdvance
            {
                TeamId = teamId, WorkflowRunId = runId, Facet = WorkflowRunDataOwnerKinds.HarnessExecution,
                Expected = expected, Present = present,
            }
            : null;

    /// <summary>A lost declaration can never be followed by a present-only advance, which would manufacture Exact over expected=0.</summary>
    private async Task MarkExecutionExpectationIndeterminateAsync(Guid teamId, Guid workflowRunId, CancellationToken cancellationToken)
    {
        if (!await _completeness.UnstateExpectationAsync(teamId, workflowRunId, WorkflowRunDataOwnerKinds.HarnessExecution, cancellationToken).ConfigureAwait(false)) return;

        _logger.LogWarning("The expectation declaration for a new harness execution of workflow run {WorkflowRunId} was not admitted, so the execution identity remains durable but the facet is unstated rather than counted from a present-only delta", workflowRunId);
    }

    private async Task NoticeRefusedExecutionAsync(CodeSpaceDbContext db, RefusedExecution refused, Exception refusal, CancellationToken cancellationToken)
    {
        if (await StillOursAsync(db, refused.TeamId, refused.AgentRunId, refused.WorkerFenceEpoch, cancellationToken).ConfigureAwait(false))
        {
            await _completeness.NoticeAsync(RefusedExecutionGap(refused, refusal), cancellationToken).ConfigureAwait(false);

            return;
        }

        await MarkExecutionsIndeterminateAsync(refused, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// How many execution identities this run should hold stops being knowable, for the reason the sibling facet
    /// already gives: the plane cannot tell a generation that went unrecorded from one that was never this worker's to
    /// write.
    ///
    /// <para>A standalone run has no statement to revise, which is not an omission: no expectation was ever declared
    /// for it, so there is nothing an un-stating could make less certain.</para>
    /// </summary>
    private async Task MarkExecutionsIndeterminateAsync(RefusedExecution refused, CancellationToken cancellationToken)
    {
        if (refused.WorkflowRunId is not { } workflowRunId) return;

        if (!await _completeness.UnstateExpectationAsync(refused.TeamId, workflowRunId, WorkflowRunDataOwnerKinds.HarnessExecution, cancellationToken).ConfigureAwait(false)) return;

        _logger.LogWarning("The harness execution generation of workflow run {WorkflowRunId} was refused after the run left this worker's fence {Fence}, so how many execution identities the run should hold is unstated rather than assumed satisfied", workflowRunId, refused.WorkerFenceEpoch);
    }

    private static WorkflowRunCaptureGap RefusedExecutionGap(RefusedExecution refused, Exception refusal)
    {
        var now = DateTimeOffset.UtcNow;

        return new WorkflowRunCaptureGap
        {
            Id = Guid.NewGuid(), TeamId = refused.TeamId, WorkflowRunId = refused.WorkflowRunId,
            AgentRunId = refused.AgentRunId,
            SubjectKind = WorkflowRunDataOwnerKinds.HarnessExecution, SubjectId = refused.ExecutionId.ToString(),
            RangeKind = CaptureGapRangeKind.Unbounded, Reason = CaptureGapReason.WriteRefused,
            ReasonDetail = $"The durable write of harness execution {refused.ExecutionId} was refused ({refusal.GetType().Name}) while the run was still this worker's at fence {refused.WorkerFenceEpoch}, so the stable execution identity has no row of its own.",
            CaptureSource = CompletenessCaptureSource, NoticedAt = now, Resolution = CaptureGapResolution.Open,
            SchemaVersion = WorkflowRunDataContract.CurrentVersion, CreatedAt = now,
        };
    }

    /// <summary>The generation whose execution identity was refused. The workflow run is nullable because a standalone launch's refusal is just as locatable a span, even though no manifest can carry its facet.</summary>
    private sealed record RefusedExecution(Guid TeamId, Guid AgentRunId, Guid? WorkflowRunId, Guid ExecutionId, long WorkerFenceEpoch);
}
