using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Contracts;
using Microsoft.Extensions.Logging;

namespace CodeSpace.Core.Services.Agents.Capture;

/// <summary>
/// The plane as a PRODUCER of <c>workflow_run_data_manifest</c> and <c>workflow_run_capture_gap</c> for the ONE facet
/// it owns, <see cref="WorkflowRunDataOwnerKinds.NativeRecord"/>. Both tables shipped with correct invariants and no
/// writer at all; this is what stops them being tables whose only writers are their tests.
///
/// <para><b>What a complete verdict here MEANS, stated so it can be checked against this file.</b> Exact —
/// RedactedExact where any frame reached storage masked — is written only when every frame this plane UNDERTOOK to
/// capture for the run became a durable record, no span of the run is known-missing, and no observer of the run died
/// inside its capture window. Nothing weaker is rounded up to it, and the ORDER the two counts advance in is what
/// makes that checkable: the expectation is DECLARED before the frames are written and their presence is stated only
/// after they are durable, so the window between them reads present below expected — a shortfall, which is not
/// complete. Advancing both counts in one statement would have left them equally short whenever the accounting was
/// lost, and the facet would have read Exact over frames nobody counted. All of this runs in a SEPARATE unit of work
/// from the frames, so a refused claim can never take a frame down with it.</para>
///
/// <para><b>What it does NOT mean</b>, because a manifest that overstated its reach would be worse than none. It is a
/// claim about the frames the runner's reader DELIVERED, never about the harness's whole output: this plane never sees
/// a line the reader dropped, and <see cref="AgentNativeRecordPump"/> already names the byte accounting that drifts
/// where a reader trims a CR or delivers an unterminated final line. And it covers exactly the frames that reached a
/// batch — a worker killed with lines still buffered was never able to state them, which is the window 0146 names as
/// the one incremental counters cannot close, bounded here by <see cref="AgentNativeRecordPump.MaxBuffered"/> frames
/// or one poll. What closes that window for a run rather than for a batch is
/// <see cref="MarkIndeterminateAsync"/>: an observer that died leaves its process attempt Running, and the run's
/// expectation stops being knowable at all rather than being read as satisfied.</para>
///
/// <para><b>The one hole a complete verdict here can still sit over, named rather than implied.</b> A re-attach whose
/// observation resumes AHEAD of the plane's recorded head never records the bytes in between, and this producer counts
/// neither of them — so that run reads complete over a span nothing holds. The honest gap for it is
/// <see cref="CaptureGapReason.ReattachTorn"/> and it is deliberately NOT produced here, because the two heads that
/// would locate it are not comparable to the byte: the spool reader trims the CR of a CRLF ending and delivers an
/// unterminated final line as whole (<c>LocalProcessRunner.SplitLines</c>), while
/// <see cref="AgentNativeRecordPump"/> reconstructs its cursor as each delivered line's byte count plus one terminator.
/// The two drift in both directions, so a gap derived from their difference would manufacture missing spans on every
/// CRLF re-attach — and a run that can never be complete is not fail-closed, it is fail-always. That divergence is
/// pinned as arithmetic by <c>LocalProcessDurableRunnerTests</c>, so this is a measured bound rather than a remembered
/// one, and the pin fails if the two quantities ever become comparable. Closing it needs the reader to state each
/// line's true offset, which is the same prerequisite the pump already names.</para>
///
/// <para><b>The expectation is discovered line by line, and declared one batch ahead.</b> 0146 contemplates a producer
/// that states what it expects before the records land. A stdout stream's total frame count cannot be known in advance
/// at all — so what this plane declares ahead is the BATCH it is about to write, which is the largest unit it can
/// honestly promise. A shortfall bigger than one batch is carried by the gap plane and by the indeterminate
/// expectation, not by a difference the two counters could reach on their own.</para>
///
/// <para><b>Which runs get no statement at all.</b> Both tables are keyed to a workflow run, so a STANDALONE Agent Run
/// — <see cref="NativeRecordCaptureHandle.WorkflowRunId"/> null — states nothing rather than stating it against an
/// invented parent, the same named keying gap 0137/0141 already carry. An absent row is the INDETERMINATE answer, and
/// <see cref="WorkflowRunDataManifest"/> already says a later per-run fold must treat it as one. A batch the contract
/// check refuses before it reaches the database also leaves no gap: that is a writer defect rather than a shortfall the
/// source suffered.</para>
///
/// <para><b>Nothing READS either table in production today, and this slice adds no reader.</b> That is deliberate
/// sequencing: wiring the terminal verdict to the manifest before a producer existed would park every run, because a
/// facet with no row and a row written before this producer are both indeterminate. What would consume it is the
/// per-run completeness fold — and, later still and separately, terminal authority — plus the audit query
/// <c>ix_workflow_run_data_manifest_incomplete</c> exists for: whose record is not complete.</para>
/// </summary>
public sealed partial class NativeRecordPlane
{
    /// <summary>Who noticed, stamped on every gap this plane records, in the versioned capture-source shape the model-call plane already uses.</summary>
    public const string CompletenessCaptureSource = "native-record-plane/v1";

    /// <summary>
    /// Commit one staged batch and account for it, declaring the batch's expectation BEFORE the frames are written and
    /// stating their presence only once they are durable. That order is the whole fail-closed argument: an accounting
    /// lost after the frames land leaves present below expected, which is not complete, where a single combined
    /// statement would have left the two equally short and read Exact over frames nobody counted.
    ///
    /// <para>Every one of the three writes runs on <see cref="IRunDataCompletenessWriter"/>'s own unit of work and is
    /// contained there. 0146 makes a completeness statement refusable (its floor check, and its refusal to raise a
    /// complete verdict over an open gap), and a claim is always safe to lose while a frame is not — so a refused claim
    /// must never be able to take a batch of frames down with it, in either direction.</para>
    ///
    /// <para>A worker tear-down is the one failure whose presence is never stated, and that is the caller's contract
    /// rather than an omission: the cancellation IS the round ending. The expectation it already declared is what keeps
    /// that honest — the batch reads as a shortfall rather than vanishing — and what covers the whole run is
    /// <see cref="MarkIndeterminateAsync"/>, reached when whoever terminalizes the execution finds this observer's
    /// process attempt still open.</para>
    /// </summary>
    private async Task CommitAsync(CodeSpaceDbContext db, NativeRecordBatch batch, CancellationToken cancellationToken)
    {
        await DeclareAsync(batch, cancellationToken).ConfigureAwait(false);

        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception refusal) when (refusal is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            await NoticeRefusedBatchAsync(batch, refusal, cancellationToken).ConfigureAwait(false);

            throw;
        }

        await AccountForAsync(batch, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>What this plane UNDERTAKES to make durable, stated before it tries, so a failure after this point is visible as a shortfall rather than as two counts that fell short together.</summary>
    private async Task DeclareAsync(NativeRecordBatch batch, CancellationToken cancellationToken)
    {
        if (Advance(batch, expected: batch.Records.Count, present: 0, masked: false) is not { } declaration) return;

        await _completeness.AdvanceAsync(declaration, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Every frame of this batch is durable, so the facet's presence advances by exactly the frames it carried — and by the redacted arm where any of them reached storage masked.</summary>
    private async Task AccountForAsync(NativeRecordBatch batch, CancellationToken cancellationToken)
    {
        if (Advance(batch, expected: 0, present: batch.Records.Count, masked: batch.Records.Any(capture => capture.Frame.Redaction == NativeRecordRedaction.Masked)) is not { } landed) return;

        await _completeness.AdvanceAsync(landed, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The batch did not become durable. Its expectation is already declared, so the shortfall is in the counts
    /// already; what this adds is the span a human can locate. Today that refusal is a log warning and a round that
    /// quietly stops capturing, which is exactly the silence the gap plane exists to break.
    ///
    /// <para>Nothing is advanced here, and that is not an omission: advancing the expectation a second time would
    /// count the batch twice and leave a shortfall of its own that no frame is missing from.</para>
    /// </summary>
    private async Task NoticeRefusedBatchAsync(NativeRecordBatch batch, Exception refusal, CancellationToken cancellationToken)
    {
        if (batch.Handle.WorkflowRunId is not { } workflowRunId || batch.Records.Count == 0) return;

        await _completeness.NoticeAsync(RefusedGap(batch, workflowRunId, refusal), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// One fold this plane may state about the batch, or null when there is nothing it may state: a run bound to no
    /// workflow run has no row to key the statement to, and a batch carrying no frames of this facet moves neither of
    /// its counts.
    /// </summary>
    private static RunDataFacetAdvance? Advance(NativeRecordBatch batch, long expected, long present, bool masked) =>
        batch.Handle.WorkflowRunId is { } workflowRunId && batch.Records.Count > 0
            ? new RunDataFacetAdvance
            {
                TeamId = batch.Handle.TeamId, WorkflowRunId = workflowRunId,
                Facet = WorkflowRunDataOwnerKinds.NativeRecord, Expected = expected, Present = present, Masked = masked,
            }
            : null;

    /// <summary>
    /// The run's expectation stops being knowable. Called when an execution reaches its terminal with a process attempt
    /// still Running — an observer that died inside the capture window, which read an unknown number of frames it never
    /// made durable. An unstated expectation is what 0146 refuses every complete verdict over, so the run fails closed
    /// rather than reading as complete over frames nobody could account for.
    /// </summary>
    private async Task MarkIndeterminateAsync(Guid teamId, Guid workflowRunId, CancellationToken cancellationToken)
    {
        if (!await _completeness.UnstateExpectationAsync(teamId, workflowRunId, WorkflowRunDataOwnerKinds.NativeRecord, cancellationToken).ConfigureAwait(false)) return;

        _logger.LogWarning("The native record capture of workflow run {WorkflowRunId} had an observer still open when its harness execution reached a terminal, so the run's frame expectation is unstated rather than assumed satisfied", workflowRunId);
    }

    /// <summary>
    /// The refused batch as a span a human can locate: this stream, this channel, from the first ordinal the batch
    /// carried. The extent is left OPEN — "from here on, and I do not know how much" — because that is what the plane
    /// knows: these frames are not in it, and whether anything after them will be is its caller's to decide, which for
    /// the pump above it means capture stops for the round.
    ///
    /// <para>The refusal's own message is deliberately NOT stored. A PostgreSQL constraint violation quotes the failing
    /// row, so copying it into a durable column would put a frame's payload somewhere the redaction discipline never
    /// looked; the exception TYPE names the class of refusal, and the pump's warning carries the rest to the log.</para>
    ///
    /// <para>No subject id: the rows this span would name are exactly the ones that do not exist, and the stream plus
    /// the ordinal is the coordinate that does locate it.</para>
    /// </summary>
    private static WorkflowRunCaptureGap RefusedGap(NativeRecordBatch batch, Guid workflowRunId, Exception refusal)
    {
        var now = DateTimeOffset.UtcNow;
        var first = batch.Records.Min(capture => capture.Frame.Ordinal);

        return new WorkflowRunCaptureGap
        {
            Id = Guid.NewGuid(), TeamId = batch.Handle.TeamId, WorkflowRunId = workflowRunId,
            SubjectKind = WorkflowRunDataOwnerKinds.NativeRecord, StreamId = batch.Handle.StreamId,
            Channel = batch.Handle.Channel, RangeKind = CaptureGapRangeKind.Ordinal, RangeStart = first,
            Reason = CaptureGapReason.WriteRefused,
            ReasonDetail = $"The durable write of {batch.Records.Count} captured frame(s) from ordinal {first} of this stream was refused ({refusal.GetType().Name}), so no record of them reached the plane.",
            CaptureSource = CompletenessCaptureSource, NoticedAt = now, Resolution = CaptureGapResolution.Open,
            SchemaVersion = WorkflowRunDataContract.CurrentVersion, CreatedAt = now,
        };
    }
}
