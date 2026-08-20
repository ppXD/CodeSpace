using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CodeSpace.Core.Services.Agents.Capture;

/// <summary>
/// The plane as a producer of <c>workflow_run_data_manifest</c> for its SECOND facet,
/// <see cref="WorkflowRunDataOwnerKinds.HarnessProcessAttempt"/>. It is the second producer of that plane at all, and
/// the first whose expectation is DECLARED rather than discovered.
///
/// <para><b>What makes this facet's expectation knowable in advance, which is why it is the honest second choice.</b>
/// A launch owes EXACTLY ONE attempt record, and that number comes from the decision to launch rather than from
/// observing anything: <see cref="INativeRecordPlane.OpenAsync"/> is the only production site that appends a process
/// attempt row, and it appends exactly one. So unlike the native-record facet — whose total no stream can state in
/// advance, leaving one batch as the largest unit that producer could honestly promise — this one can state its whole
/// obligation before the fact it describes exists. A RESUMED opening owes none, because it appends no row
/// (<see cref="NativeRecordCaptureRequest.Resume"/>); it is observed by
/// <see cref="INativeRecordExecutionPlane.ReopenAsync"/>, which states nothing here.</para>
///
/// <para><b>What a complete verdict here MEANS.</b> Every process this plane opened a capture for has a durable attempt
/// row. Nothing weaker is rounded up to it, and the ORDER is what makes that checkable: the expectation is declared
/// BEFORE the row is written and its presence stated only once the row is durable, so the window between them reads
/// present below expected — a shortfall, which is not complete. Advancing both counts in one statement would have left
/// them equally short whenever the accounting was lost, and the facet would have read Exact over a process nobody
/// counted. Only the verbatim arm is reachable: an attempt row is execution identity, never captured bytes, so
/// <see cref="RunDataFacetAdvance.Masked"/> is always false and there is nothing in it to redact.</para>
///
/// <para><b>What it does NOT mean.</b> It is a claim about the launches this plane was ASKED to open, never about every
/// process the executor started: an opening the executor never requested, and one whose Agent Run row could not be read
/// at all (<c>LoadRunScopeAsync</c> returning null, which happens before anything is declared), leave this facet with no
/// statement — and an absent statement is the indeterminate answer, not a complete one. A STANDALONE Agent Run is the
/// same case by keying: the manifest is keyed to a workflow run, so a run bound to none states nothing rather than
/// stating it against an invented parent, the same named gap 0137/0141 already carry.</para>
///
/// <para><b>Why a refusal is not always a missing record, which is the one thing this producer must get right.</b> 0137
/// refuses a superseded worker's attempt insert BY DESIGN — that is the intended outcome of reclaim-for-reattach, not a
/// lost row — and from here the plane cannot tell that case from a genuine loss. So the two are separated by asking the
/// only question that distinguishes them: is the run still this worker's at the fence it launched under? If it is, the
/// row this worker owed is genuinely absent and becomes a locatable gap. If the fence has moved, nothing here can show
/// that any record is missing, so the facet's expectation is UN-STATED instead — indeterminate, which 0146 refuses
/// every complete verdict over, rather than a shortfall the plane cannot substantiate or a gap it cannot back up.</para>
///
/// <para><b>Nothing READS the manifest in production, and this slice adds no reader.</b> Wiring a terminal verdict to it
/// while most facets still have no producer would park every run, because a facet with no statement is indeterminate.
/// </para>
/// </summary>
public sealed partial class NativeRecordPlane
{
    /// <summary>
    /// Declare what the launch owes, write the row, then state that it landed — in that order, which is the whole
    /// fail-closed argument for this facet. Every completeness write runs on
    /// <see cref="RunData.IRunDataCompletenessWriter"/>'s own unit of work and is contained there, so a refused claim
    /// can never take the attempt row down with it in either direction.
    /// </summary>
    private async Task AppendAttemptAsync(CodeSpaceDbContext db, NativeRecordCaptureRequest request, Guid? workflowRunId, Guid attemptId, CancellationToken cancellationToken)
    {
        await DeclareAttemptAsync(request.TeamId, workflowRunId, cancellationToken).ConfigureAwait(false);

        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception refusal) when (refusal is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            if (workflowRunId is { } runId)
                await NoticeRefusedAttemptAsync(db, new RefusedAttempt(request.TeamId, request.AgentRunId, runId, attemptId, request.WorkerFenceEpoch), refusal, cancellationToken).ConfigureAwait(false);

            throw;
        }

        await AccountForAttemptAsync(request.TeamId, workflowRunId, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>What the launch UNDERTAKES to make durable — one process record — stated before it tries, so a failure after this point is visible as a shortfall rather than as two counts that fell short together.</summary>
    private async Task DeclareAttemptAsync(Guid teamId, Guid? workflowRunId, CancellationToken cancellationToken)
    {
        if (AttemptAdvance(teamId, workflowRunId, expected: 1, present: 0) is not { } declaration) return;

        await _completeness.AdvanceAsync(declaration, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The process record is durable, so the facet's presence advances by the one row the launch owed.</summary>
    private async Task AccountForAttemptAsync(Guid teamId, Guid? workflowRunId, CancellationToken cancellationToken)
    {
        if (AttemptAdvance(teamId, workflowRunId, expected: 0, present: 1) is not { } landed) return;

        await _completeness.AdvanceAsync(landed, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>One fold this plane may state about the launch, or null when there is nothing it may state: a run bound to no workflow run has no row to key the statement to.</summary>
    private static RunDataFacetAdvance? AttemptAdvance(Guid teamId, Guid? workflowRunId, long expected, long present) =>
        workflowRunId is { } runId
            ? new RunDataFacetAdvance
            {
                TeamId = teamId, WorkflowRunId = runId, Facet = WorkflowRunDataOwnerKinds.HarnessProcessAttempt,
                Expected = expected, Present = present,
            }
            : null;

    /// <summary>
    /// The refusal, separated into the two things it can be. A run still held by this worker at the fence it launched
    /// under means the row it owed is genuinely absent, and that span becomes a gap. A fence that has moved means 0137
    /// refused a worker that no longer speaks for the run — the intended outcome — and nothing here can show that any
    /// record is missing, so the expectation is un-stated instead of a gap being manufactured for it.
    /// </summary>
    private async Task NoticeRefusedAttemptAsync(CodeSpaceDbContext db, RefusedAttempt refused, Exception refusal, CancellationToken cancellationToken)
    {
        if (await StillOursAsync(db, refused, cancellationToken).ConfigureAwait(false))
        {
            await _completeness.NoticeAsync(RefusedAttemptGap(refused, refusal), cancellationToken).ConfigureAwait(false);

            return;
        }

        await MarkAttemptsIndeterminateAsync(refused, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Whether the run is still this worker's at the fence it launched under — the one question that separates a lost
    /// row from a superseded writer. Read AFTER the refusal deliberately: asking before it would answer about a moment
    /// the refusal had not happened in yet.
    /// </summary>
    private static async Task<bool> StillOursAsync(CodeSpaceDbContext db, RefusedAttempt refused, CancellationToken cancellationToken) =>
        await db.AgentRun.AsNoTracking()
            .AnyAsync(run => run.TeamId == refused.TeamId && run.Id == refused.AgentRunId && run.FenceEpoch == refused.WorkerFenceEpoch, cancellationToken)
            .ConfigureAwait(false);

    /// <summary>
    /// The refused row as a span a human can locate: the attempt identity this launch minted and could not make
    /// durable. The range is <see cref="CaptureGapRangeKind.Unbounded"/> because one identity record either exists or
    /// does not — an ordinal or byte range would invent a coordinate system this span has no position in, and the
    /// subject id is what actually locates it.
    ///
    /// <para>The refusal's own message is deliberately NOT stored, for the reason the refused-batch gap already gives:
    /// a PostgreSQL constraint violation quotes the failing row, and this row carries the runner locator. The exception
    /// TYPE names the class of refusal and the caller's log carries the rest.</para>
    /// </summary>
    private static WorkflowRunCaptureGap RefusedAttemptGap(RefusedAttempt refused, Exception refusal)
    {
        var now = DateTimeOffset.UtcNow;

        return new WorkflowRunCaptureGap
        {
            Id = Guid.NewGuid(), TeamId = refused.TeamId, WorkflowRunId = refused.WorkflowRunId,
            SubjectKind = WorkflowRunDataOwnerKinds.HarnessProcessAttempt, SubjectId = refused.AttemptId.ToString(),
            RangeKind = CaptureGapRangeKind.Unbounded, Reason = CaptureGapReason.WriteRefused,
            ReasonDetail = $"The durable write of this harness process attempt was refused ({refusal.GetType().Name}) while the run was still this worker's at fence {refused.WorkerFenceEpoch}, so the process it identifies has no record of its own.",
            CaptureSource = CompletenessCaptureSource, NoticedAt = now, Resolution = CaptureGapResolution.Open,
            SchemaVersion = WorkflowRunDataContract.CurrentVersion, CreatedAt = now,
        };
    }

    /// <summary>
    /// How many processes this run should hold a record for stops being knowable. Reached when the attempt write was
    /// refused after the run was reclaimed: the plane cannot tell whether a process ran unrecorded or whether the row
    /// was never this worker's to write, and an expectation nobody could establish must not read as complete.
    /// </summary>
    private async Task MarkAttemptsIndeterminateAsync(RefusedAttempt refused, CancellationToken cancellationToken)
    {
        if (!await _completeness.UnstateExpectationAsync(refused.TeamId, refused.WorkflowRunId, WorkflowRunDataOwnerKinds.HarnessProcessAttempt, cancellationToken).ConfigureAwait(false)) return;

        _logger.LogWarning("The harness process attempt of workflow run {WorkflowRunId} was refused after the run left this worker's fence {Fence}, so how many processes the run should hold a record for is unstated rather than assumed satisfied", refused.WorkflowRunId, refused.WorkerFenceEpoch);
    }

    /// <summary>The launch whose process record was refused, carried as one value so the refusal path stays inside the parameter cap.</summary>
    private sealed record RefusedAttempt(Guid TeamId, Guid AgentRunId, Guid WorkflowRunId, Guid AttemptId, long WorkerFenceEpoch);
}
