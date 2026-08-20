using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
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
/// inside its capture window. Nothing weaker is rounded up to it: the expectation and the presence advance in the same
/// statement as each other but in a SEPARATE transaction from the frames, so a refused batch advances the expectation
/// and not the presence, and the shortfall is visible in the counts as well as in the gap.</para>
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
/// CRLF re-attach — and a run that can never be complete is not fail-closed, it is fail-always. Closing this needs the
/// reader to state each line's true offset, which is the same prerequisite the pump already names.</para>
///
/// <para><b>The expectation here is DISCOVERED, not declared.</b> 0146 contemplates a producer that states what it
/// expects before the records land, so a death in between leaves present below expected. This plane cannot: a stdout
/// stream's frame count is learned line by line, so declaring an expectation ahead of the batch would only re-state
/// the batch. What carries a shortfall is therefore the gap plane and the indeterminate expectation, not a difference
/// the two counters could reach on their own.</para>
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
    /// Commit one staged batch and account for it. The accounting is a SEPARATE transaction from the frames on purpose:
    /// 0146 makes a completeness statement refusable (its floor check, and its refusal to raise a complete verdict over
    /// an open gap), and a claim is always safe to lose while a frame is not — so a refused claim must never be able to
    /// take a batch of frames down with it.
    ///
    /// <para>A worker tear-down is the one failure that states NOTHING, and that is the caller's contract rather than an
    /// omission: the cancellation IS the round ending, so the batch is neither counted nor recorded as missing. What
    /// covers that run is <see cref="MarkIndeterminateAsync"/>, reached when whoever terminalizes the execution finds
    /// this observer's process attempt still open.</para>
    /// </summary>
    private async Task CommitAsync(CodeSpaceDbContext db, NativeRecordBatch batch, CancellationToken cancellationToken)
    {
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

    /// <summary>Every frame of this batch is durable, so the facet's expectation and its presence both advance by exactly the frames it carried.</summary>
    private async Task AccountForAsync(NativeRecordBatch batch, CancellationToken cancellationToken)
    {
        if (Scoped(batch) is not { } advance) return;

        await StateAsync(advance with { Present = batch.Records.Count, Masked = batch.Records.Any(capture => capture.Frame.Redaction == NativeRecordRedaction.Masked) }, null, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The batch did not become durable: the facet's expectation advances by the frames it undertook, its presence does
    /// not, and the span they occupied becomes a gap a human can locate. Today that refusal is a log warning and a round
    /// that quietly stops capturing, which is exactly the silence the gap plane exists to break.
    /// </summary>
    private async Task NoticeRefusedBatchAsync(NativeRecordBatch batch, Exception refusal, CancellationToken cancellationToken)
    {
        if (Scoped(batch) is not { } advance) return;

        await StateAsync(advance, RefusedGap(batch, advance.WorkflowRunId, refusal), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The scope a statement about this batch would be made in, or null when there is nothing this plane may state: a
    /// run bound to no workflow run has no row to key either table to, and a batch carrying no frames of this facet
    /// moves neither of its counts.
    /// </summary>
    private static CompletenessAdvance? Scoped(NativeRecordBatch batch) =>
        batch.Handle.WorkflowRunId is { } workflowRunId && batch.Records.Count > 0
            ? new CompletenessAdvance(batch.Handle.TeamId, workflowRunId, batch.Records.Count, 0, false)
            : null;

    /// <summary>
    /// The run's expectation stops being knowable. Called when an execution reaches its terminal with a process attempt
    /// still Running — an observer that died inside the capture window, which read an unknown number of frames it never
    /// made durable. An unstated expectation is what 0146 refuses every complete verdict over, so the run fails closed
    /// rather than reading as complete over frames nobody could account for.
    ///
    /// <para>Idempotent by its own predicate: a statement already indeterminate is left alone rather than re-revised,
    /// and a run this plane never stated anything about gets no row invented for it — an absent statement is already
    /// the indeterminate answer.</para>
    /// </summary>
    private async Task MarkIndeterminateAsync(Guid teamId, Guid workflowRunId, CancellationToken cancellationToken) =>
        await UnderRendezvousAsync(teamId, workflowRunId, async db =>
        {
            var marked = await db.Database.ExecuteSqlAsync($$"""
                UPDATE workflow_run_data_manifest SET
                    expected_record_count = NULL,
                    known_missing_count = GREATEST(known_missing_count, workflow_run_capture_gap_open_count(team_id, workflow_run_id, facet)),
                    verdict = CASE WHEN GREATEST(known_missing_count, workflow_run_capture_gap_open_count(team_id, workflow_run_id, facet)) > 0
                                   THEN 'Partial' ELSE 'LegacyUnknown' END,
                    revision = revision + 1,
                    last_modified_at = GREATEST(last_modified_at, {{DateTimeOffset.UtcNow}})
                WHERE team_id = {{teamId}} AND workflow_run_id = {{workflowRunId}}
                  AND facet = {{WorkflowRunDataOwnerKinds.NativeRecord}} AND expected_record_count IS NOT NULL
                """, cancellationToken).ConfigureAwait(false);

            if (marked > 0)
                _logger.LogWarning("The native record capture of workflow run {WorkflowRunId} had an observer still open when its harness execution reached a terminal, so the run's frame expectation is unstated rather than assumed satisfied", workflowRunId);
        }, cancellationToken).ConfigureAwait(false);

    /// <summary>Record the gap this advance noticed, if it noticed one, and fold the advance into the run's statement for this facet — under one rendezvous, so the statement is computed over gaps that cannot change underneath it.</summary>
    private async Task StateAsync(CompletenessAdvance advance, WorkflowRunCaptureGap? gap, CancellationToken cancellationToken) =>
        await UnderRendezvousAsync(advance.TeamId, advance.WorkflowRunId, async db =>
        {
            if (gap is not null)
            {
                db.WorkflowRunCaptureGap.Add(gap);
                await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            }

            await AdvanceStatementAsync(db, advance, cancellationToken).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);

    /// <summary>
    /// One transaction holding 0146's per-run rendezvous lock for its whole length, on this plane's OWN scope.
    ///
    /// <para>The lock is taken EXPLICITLY here rather than left to the guards that also take it, and that is what makes
    /// the statement below unrefusable. Its verdict is computed from the run's open gaps, and 0146 re-probes them under
    /// the lock inside the trigger — so a gap committing between an unlocked probe and the trigger's would refuse a
    /// statement whose counts are a DELTA, and a lost delta silently understates the run's expectation forever. Taking
    /// the lock first means the probe and the write see the same set.</para>
    ///
    /// <para>Contained, always: a claim about the record is safe to lose, and losing one may not change what an Agent
    /// Run resolves to.</para>
    /// </summary>
    private async Task UnderRendezvousAsync(Guid teamId, Guid workflowRunId, Func<CodeSpaceDbContext, Task> write, CancellationToken cancellationToken)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<CodeSpaceDbContext>();

            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

            await db.Database.ExecuteSqlAsync($"SELECT workflow_run_data_completeness_lock({teamId}, {workflowRunId})", cancellationToken).ConfigureAwait(false);

            await write(db).ConfigureAwait(false);

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(exception, "The native record plane could not state the completeness of workflow run {WorkflowRunId}; the statement is lost, the frames it describes are untouched, and the run resolves exactly as it does with no statement at all", workflowRunId);
        }
    }

    /// <summary>
    /// Fold one advance into the run's statement for this facet: insert it when the facet has none, add to it when it
    /// has. The verdict is computed HERE rather than proposed by the caller, so this statement never offers 0146 a
    /// claim it would refuse — complete is proposed only over a determinate expectation that is fully present, with
    /// nothing known-missing in this facet and no open gap anywhere in the run.
    ///
    /// <para>An expectation that is already NULL absorbs: <c>NULL + n</c> is NULL, so a run marked indeterminate stays
    /// indeterminate however many later batches land, and its existing not-complete verdict is carried rather than
    /// recomputed. A facet whose known-missing count has been raised by a gap stays Partial: this producer never
    /// recovers a gap, and a later slice that does must lower this count too rather than expect a raise to follow.</para>
    /// </summary>
    private static Task AdvanceStatementAsync(CodeSpaceDbContext db, CompletenessAdvance advance, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;

        return db.Database.ExecuteSqlAsync($$"""
            INSERT INTO workflow_run_data_manifest AS statement (
                id, team_id, workflow_run_id, facet, expected_record_count, present_record_count,
                known_missing_count, verdict, revision, schema_version, created_at, last_modified_at)
            SELECT {{Guid.NewGuid()}}, {{advance.TeamId}}, {{advance.WorkflowRunId}}, {{WorkflowRunDataOwnerKinds.NativeRecord}},
                   {{advance.Expected}}, {{advance.Present}}, gaps.here,
                   CASE WHEN gaps.anywhere OR gaps.here > 0 OR {{advance.Present}} < {{advance.Expected}} THEN 'Partial'
                        WHEN {{advance.Masked}} THEN 'RedactedExact' ELSE 'Exact' END,
                   1, {{WorkflowRunDataContract.CurrentVersion}}, {{now}}, {{now}}
            FROM (SELECT workflow_run_capture_gap_open_count({{advance.TeamId}}, {{advance.WorkflowRunId}}, {{WorkflowRunDataOwnerKinds.NativeRecord}}::varchar) AS here,
                         EXISTS (SELECT 1 FROM workflow_run_capture_gap
                                 WHERE team_id = {{advance.TeamId}} AND workflow_run_id = {{advance.WorkflowRunId}}
                                   AND resolution = 'Open') AS anywhere) AS gaps
            ON CONFLICT (team_id, workflow_run_id, facet) DO UPDATE SET
                expected_record_count = statement.expected_record_count + {{advance.Expected}},
                present_record_count = statement.present_record_count + {{advance.Present}},
                known_missing_count = GREATEST(statement.known_missing_count, excluded.known_missing_count),
                verdict = CASE
                    WHEN statement.expected_record_count IS NULL THEN statement.verdict
                    WHEN excluded.verdict = 'Partial' THEN 'Partial'
                    WHEN statement.present_record_count + {{advance.Present}} < statement.expected_record_count + {{advance.Expected}} THEN 'Partial'
                    WHEN GREATEST(statement.known_missing_count, excluded.known_missing_count) > 0 THEN 'Partial'
                    WHEN statement.verdict = 'RedactedExact' OR excluded.verdict = 'RedactedExact' THEN 'RedactedExact'
                    ELSE 'Exact' END,
                revision = statement.revision + 1,
                last_modified_at = GREATEST(statement.last_modified_at, {{now}})
            """, cancellationToken);
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

    /// <summary>One fold into a run's statement for the native-record facet: the scope it is made in, how many frames were undertaken, how many of them landed, and whether any that landed reached storage masked.</summary>
    private sealed record CompletenessAdvance(Guid TeamId, Guid WorkflowRunId, long Expected, long Present, bool Masked);
}
