using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.RunData;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Contracts;
using Microsoft.Extensions.Logging;

namespace CodeSpace.Core.Services.Agents.Capture;

/// <summary>
/// The plane as a PRODUCER of <c>workflow_run_data_manifest</c> and <c>workflow_run_capture_gap</c> for the native rows
/// and semantic projections its one batch makes durable. Both tables shipped with correct invariants and no
/// writer at all; this is what stops them being tables whose only writers are their tests.
///
/// <para><b>What a complete verdict here MEANS, stated so it can be checked against this file.</b> Exact —
/// RedactedExact where any frame reached storage masked — is written only when every frame this plane UNDERTOOK to
/// capture for the run became a durable record, no span of the run is known-missing, and no observer of the run died
/// inside its capture window. Nothing weaker is rounded up to it, and the ORDER the two counts advance in is what
/// makes that checkable: the expectation is DECLARED before the frames are written and their presence is stated only
/// after they are durable, so the window between them reads present below expected — a shortfall, which is not
/// complete. Advancing both counts in one statement would have left them equally short whenever the accounting was
/// lost, and the facet would have read Exact over frames nobody counted. The MIRROR of that is the declaration being
/// the lost one, and it does not fail closed by itself: a presence stated alone lands over an expectation that never
/// counted the batch — expected=0 on a fresh statement, which 0148 reads as Exact — so a batch whose declaration was
/// not admitted un-states the facet instead of accounting for itself. And because the redacted arm is a claim about
/// BYTES rather than counts, the masked observation latches in a column of its own (0166): once any frame of the run
/// reached storage masked, no later unmasked batch can read the run back as verbatim. All of this runs in a SEPARATE
/// unit of work from the frames, so a refused claim can never take a frame down with it.</para>
///
/// <para><b>What it does NOT mean</b>, because a manifest that overstated its reach would be worse than none. It is a
/// claim about the frames the runner's reader DELIVERED, never about bytes the durable source did not retain. And it covers exactly the frames that reached a
/// batch — a worker killed with lines still buffered was never able to state them, which is the window 0146 names as
/// the one incremental counters cannot close, bounded here by <see cref="AgentNativeRecordPump.MaxBuffered"/> frames
/// or one poll. What closes that window for a run rather than for a batch is
/// <see cref="MarkIndeterminateAsync"/>: an observer that died leaves its process attempt Running, and the run's
/// expectation stops being knowable at all rather than being read as satisfied.</para>
///
/// <para><b>The re-attach tear is closed physically, but not rounded up administratively.</b> Durable stdout frames
/// now carry reader-authored half-open byte ranges, so a replacement observes from the lesser of the application head
/// and this plane's recorded head. Frames below the application head repair only this plane; they never re-enter the
/// normalized timeline. A second worker replacement resumes that repair at the exact recorded end, while its
/// application checkpoint remains monotonic. Backfill does not re-declare an expectation a refused batch already
/// declared, and this producer does not silently close the corresponding <see cref="CaptureGapReason.WriteRefused"/>
/// gap. Admitting the recovered range into that historical expectation requires a separate digest/ordinal-aware
/// recovery transition; until then the open gap keeps the terminal verdict conservative.</para>
///
/// <para><b>The expectation is discovered line by line, and declared one batch ahead.</b> 0146 contemplates a producer
/// that states what it expects before the records land. A stdout stream's total frame count cannot be known in advance
/// at all — so what this plane declares ahead is the BATCH it is about to write, which is the largest unit it can
/// honestly promise. A shortfall bigger than one batch is carried by the gap plane and by the indeterminate
/// expectation, not by a difference the two counters could reach on their own.</para>
///
/// <para><b>Which runs get no statement at all.</b> The MANIFEST is keyed to a workflow run, so a STANDALONE Agent Run
/// — <see cref="NativeRecordCaptureHandle.WorkflowRunId"/> null — states no facet rather than stating one against an
/// invented parent, the same named keying gap 0137/0141 already carry. An absent row is the INDETERMINATE answer, and
/// <see cref="WorkflowRunDataManifest"/> already says a later per-run fold must treat it as one. Its GAPS are a
/// different matter since 0184: they are keyed to the run that owns the record, so a standalone run's known-missing
/// spans are recorded rather than swallowed — the silence a gap plane exists to break does not become acceptable
/// because the run has no workflow parent. A batch the contract check refuses before it reaches the database still
/// leaves no gap: that is a writer defect rather than a shortfall the source suffered.</para>
///
/// <para><b>No authority reads either table.</b> A bounded, team-scoped Agent Run operator summary observes the
/// capture gaps that NAME the run, failure-contained from the authoritative run summary. It neither reads the manifest nor
/// changes completion, terminal, planning, oracle or routing behavior. That sequencing remains deliberate: wiring a
/// terminal verdict to the manifest while most facets have no producer would park every run, because a facet with no
/// row and a row written before this producer are both indeterminate. What would consume it authoritatively is the
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
        var declared = batch.BackfillsDeclaredFrames || await AdvanceAllAsync(Advances(batch, declaration: true), cancellationToken).ConfigureAwait(false);

        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception refusal) when (refusal is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            await NoticeRefusedBatchAsync(batch, refusal, cancellationToken).ConfigureAwait(false);

            throw;
        }

        if (declared) await AccountForAsync(batch, cancellationToken).ConfigureAwait(false);
        else await MarkDeclarationLostAsync(batch, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// What this plane UNDERTAKES to make durable, stated before it tries, so a failure after this point is visible as
    /// a shortfall rather than as two counts that fell short together. Returns whether the claim was admitted, because
    /// a lost declaration may not be followed by a presence.
    ///
    /// A lost declaration can never be followed by a present-only advance, which would manufacture Exact over
    /// expected=0. The batch's frames are durable and stay so; what stops being knowable is how many the run owes, and
    /// an unstated expectation is what 0146 refuses every complete verdict over.
    ///
    /// <para>A batch this plane may state nothing about — a run bound to no workflow run, or a batch carrying no frames
    /// of this facet — is not an un-stating either: there was no expectation to lose.</para>
    /// </summary>
    private async Task MarkDeclarationLostAsync(NativeRecordBatch batch, CancellationToken cancellationToken)
    {
        if (batch.Handle.WorkflowRunId is not { } workflowRunId) return;

        foreach (var facet in Advances(batch, declaration: true).Select(advance => advance.Facet))
        {
            if (!await _completeness.UnstateExpectationAsync(batch.Handle.TeamId, workflowRunId, facet, cancellationToken).ConfigureAwait(false)) continue;

            _logger.LogWarning("The {Facet} expectation declaration for a captured batch of workflow run {WorkflowRunId} was not admitted, so its durable records remain uncounted and the facet is unstated rather than advanced from a present-only delta", facet, workflowRunId);
        }
    }

    /// <summary>Every frame of this batch is durable, so the facet's presence advances by exactly the frames it carried — and by the redacted arm where any of them reached storage masked.</summary>
    private async Task AccountForAsync(NativeRecordBatch batch, CancellationToken cancellationToken)
    {
        await AdvanceAllAsync(Advances(batch, declaration: false), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The batch did not become durable. Where an expectation was declared the shortfall is in the counts already; what
    /// this adds is the span a human can locate. Today that refusal is a log warning and a round that quietly stops
    /// capturing, which is exactly the silence the gap plane exists to break — and a run with no workflow parent, whose
    /// facet no manifest could carry, is the run where that silence was total.
    ///
    /// <para>Nothing is advanced here, and that is not an omission: advancing the expectation a second time would
    /// count the batch twice and leave a shortfall of its own that no frame is missing from.</para>
    /// </summary>
    private async Task NoticeRefusedBatchAsync(NativeRecordBatch batch, Exception refusal, CancellationToken cancellationToken)
    {
        if (batch.Records.Count == 0) return;

        await _completeness.NoticeAsync(RefusedGap(batch, refusal), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// One fold this plane may state about the batch, or null when there is nothing it may state: a run bound to no
    /// workflow run has no row to key the statement to, and a batch carrying no frames of this facet moves neither of
    /// its counts.
    /// </summary>
    private async Task<bool> AdvanceAllAsync(IReadOnlyList<RunDataFacetAdvance> advances, CancellationToken cancellationToken)
    {
        if (advances.Count == 0) return true;
        if (_completeness is IRunDataCompletenessBatchWriter batchWriter)
            return await batchWriter.AdvanceBatchAsync(advances, cancellationToken).ConfigureAwait(false);

        var admitted = true;
        foreach (var advance in advances)
            admitted = await _completeness.AdvanceAsync(advance, cancellationToken).ConfigureAwait(false) && admitted;

        return admitted;
    }

    /// <summary>
    /// The facets this transaction itself makes durable. Semantic-event applicability is conditional per run: a batch
    /// with no projection never acquires it. A backfill calls only the presence arm; the database skips that arm where
    /// no historical declaration exists, so deploying this producer cannot invent an obligation for an older run.
    /// </summary>
    private static IReadOnlyList<RunDataFacetAdvance> Advances(NativeRecordBatch batch, bool declaration)
    {
        if (batch.Handle.WorkflowRunId is not { } workflowRunId) return [];

        var advances = new List<RunDataFacetAdvance>(2);
        Add(WorkflowRunDataOwnerKinds.NativeRecord, batch.Records.Count,
            !declaration && batch.Records.Any(capture => capture.Frame.Redaction == NativeRecordRedaction.Masked));
        Add(WorkflowRunDataOwnerKinds.SemanticEvent, batch.Events.Count,
            !declaration && batch.Events.Any(projection => projection.ProjectionQuality == SemanticProjectionQuality.RedactedExact));
        return advances;

        void Add(string facet, int count, bool masked)
        {
            if (count == 0) return;
            advances.Add(new RunDataFacetAdvance
            {
                TeamId = batch.Handle.TeamId, WorkflowRunId = workflowRunId, Facet = facet,
                Expected = declaration ? count : 0, Present = declaration ? 0 : count, Masked = masked,
            });
        }
    }

    /// <summary>
    /// The run's expectation stops being knowable. Called when an execution reaches its terminal with a process attempt
    /// still Running — an observer that died inside the capture window, which read an unknown number of frames it never
    /// made durable. An unstated expectation is what 0146 refuses every complete verdict over, so the run fails closed
    /// rather than reading as complete over frames nobody could account for.
    /// </summary>
    private async Task MarkIndeterminateAsync(Guid teamId, Guid workflowRunId, CancellationToken cancellationToken)
    {
        foreach (var facet in new[] { WorkflowRunDataOwnerKinds.NativeRecord, WorkflowRunDataOwnerKinds.SemanticEvent })
        {
            if (!await _completeness.UnstateExpectationAsync(teamId, workflowRunId, facet, cancellationToken).ConfigureAwait(false)) continue;

            _logger.LogWarning("The {Facet} capture of workflow run {WorkflowRunId} had an observer still open when its harness execution reached a terminal, so its expectation is unstated rather than assumed satisfied", facet, workflowRunId);
        }
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
    private static WorkflowRunCaptureGap RefusedGap(NativeRecordBatch batch, Exception refusal)
    {
        var now = DateTimeOffset.UtcNow;
        var first = batch.Records.Min(capture => capture.Frame.Ordinal);

        return new WorkflowRunCaptureGap
        {
            Id = Guid.NewGuid(), TeamId = batch.Handle.TeamId, WorkflowRunId = batch.Handle.WorkflowRunId,
            AgentRunId = batch.Handle.AgentRunId, HarnessExecutionId = batch.Handle.ExecutionId,
            HarnessProcessAttemptId = batch.Handle.AttemptId, AttemptWorkerFenceEpoch = batch.Handle.WorkerFenceEpoch,
            SubjectKind = WorkflowRunDataOwnerKinds.NativeRecord, StreamId = batch.Handle.StreamId,
            Channel = batch.Handle.Channel, RangeKind = CaptureGapRangeKind.Ordinal, RangeStart = first,
            Reason = CaptureGapReason.WriteRefused,
            ReasonDetail = $"The durable write of {batch.Records.Count} captured frame(s) from ordinal {first} of this stream was refused ({refusal.GetType().Name}), so no record of them reached the plane.",
            CaptureSource = CompletenessCaptureSource, NoticedAt = now, Resolution = CaptureGapResolution.Open,
            SchemaVersion = WorkflowRunDataContract.CurrentVersion, CreatedAt = now,
        };
    }
}
