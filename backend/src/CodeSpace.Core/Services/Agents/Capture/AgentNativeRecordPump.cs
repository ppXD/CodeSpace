using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CodeSpace.Core.Services.Agents.Reduction;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Contracts;
using Microsoft.Extensions.Logging;

namespace CodeSpace.Core.Services.Agents.Capture;

/// <summary>
/// The executor's line pump for the native-record plane: the redacted frame becomes a native record BEFORE the harness
/// is asked to interpret it, and whatever the harness's parser then yields becomes a semantic event projected FROM that
/// record. A line the parser drops still has its record — that is the whole point, and it is what makes the durable
/// stream stop being lossy where the normalized log alone is.
///
/// <para><b>Ordering.</b> The frame is captured first, the parse is attempted second, and the record carries the
/// outcome of that attempt. What the ordering buys is not "the bytes hit the disk before the parser ran" — records and
/// their projections are written together in one batched transaction — but the two things that actually matter: a frame
/// cannot be dropped by the parser's opinion of it, and a projection can never be durable while the frame it cites is
/// not.</para>
///
/// <para><b>Two projections, two fidelities.</b> What the parser yields is a NORMALIZATION and is projected
/// <see cref="SemanticProjectionQuality.Derived"/>. Beside it, <see cref="GroundedFrameProjector"/> asks the harness
/// whether the captured bytes ARE one of its own structured session records, and a frame that is one also yields an
/// EXACTLY GROUNDED projection — the only kind the reduction takes a named fact from. Neither replaces the other, and
/// the grounded reader is asked independently of the parser, so a fact the bytes stated is not the parser's to lose.</para>
///
/// <para><b>One opening, one channel.</b> A pump reads a single stream. The harness's stdout goes through
/// <see cref="CaptureAsync"/>, which parses; its stderr goes through <see cref="CaptureDiagnosticAsync"/>, which
/// records the frame and asks no parser anything, because a parser written for one channel's protocol would read the
/// other's diagnostics as events nobody emitted. Stderr is therefore a second opening of the same process on its own
/// stream, never a second meaning for this one.</para>
///
/// <para><b>Retention is O(1) in the event count.</b> The buffer is capped and flushed, exactly as
/// <c>BufferedEventWriter</c> is, because a long run must not be able to exhaust the heap here — the two accumulators
/// #1479 and #1489 removed are not to be replaced by a third. Per line the pump holds one record and its events, and
/// nothing that survives a flush.</para>
///
/// <para><b>The reduction rides the write, not beside it.</b> A batch is folded into
/// <see cref="HarnessReductionSink"/> immediately BEFORE it is persisted, and the checkpoint that fold produces is
/// written in the batch's OWN transaction. So the durable position can neither lead the frames it claims nor lag
/// them, and a replaced worker resumes from exactly the prefix that is actually recorded — which is what a re-attach
/// has until now had no way to recover.</para>
///
/// <para><b>The seam re-delivers, and the pump drops what it re-delivers.</b> A frame becomes durable at its batch
/// write; the spool position a re-attach resumes from is persisted after that write, so the records legitimately run
/// AHEAD of it and the span between the two is delivered a second time to the resumed observation. A resumed opening
/// therefore starts its cursor where the observation actually resumes rather than at the recorded head, so a
/// re-delivered line is described at the position its first record already used, and it records nothing below
/// <see cref="NativeRecordCaptureOpening.RecordedHead"/> — the fold counts each record and chains its digest, so a
/// second copy would not be a harmless duplicate but a stored state witnessing a prefix the process never produced.</para>
///
/// <para><b>It cannot change what the run resolves to.</b> A plane that will not open, a reduction that will not
/// resume, and a write that will not land each disable their half for the round with a warning; the harness's own
/// output path is untouched, and nothing reads the record and event tables yet. Nor does capture SWALLOW anything: a
/// parser that throws has its frame recorded with the reason and the throw then propagates exactly as it did before
/// this plane existed. Containing it here would have made the run's outcome depend on whether a shadow plane happened
/// to be deployed, which is the one thing a dual write may not do.</para>
/// </summary>
internal sealed class AgentNativeRecordPump
{
    /// <summary>Buffer cap, matching <c>BufferedEventWriter</c>: the per-poll checkpoint is the normal flush trigger and this bounds memory between two of them.</summary>
    internal const int MaxBuffered = 256;

    /// <summary>Namespace every event type this projector emits is minted under. A URI rather than a bare word so a harness-specific or operator-defined event can never collide with a first-party one.</summary>
    internal const string EventTypeNamespace = "https://codespace.dev/agent/v1/";

    /// <summary>The type recorded for a frame that is not a JSON object naming its own kind. Total by construction, so classifying a frame can never be the thing that throws.</summary>
    internal const string UnstructuredNativeType = "text-line";

    /// <summary>Reason code stamped on a record whose parse threw. Queryable through <c>ix_workflow_run_native_record_unprojected</c>, which is how "which frames can we no longer interpret" stops being unanswerable.</summary>
    internal const string NormalizationThrewErrorCode = "normalization.parser-threw";

    private readonly INativeRecordPlane? _plane;
    private readonly SecretRedactor _redactor;
    private readonly HarnessReductionSink _reduction;
    private readonly ILogger _logger;
    private readonly List<NativeRecordCapture> _records = new();
    private readonly List<AgentSemanticEventV1> _events = new();

    private NativeRecordCaptureHandle? _handle;
    private long _ordinal;
    private long _sourceOffset;
    private readonly long _recordedHead;

    private AgentNativeRecordPump(INativeRecordPlane? plane, NativeRecordCaptureOpening? opening, SecretRedactor redactor, HarnessReductionSink reduction, ILogger logger)
    {
        _plane = plane;
        _handle = opening?.Handle;
        _sourceOffset = opening?.SourceHead ?? 0;
        _recordedHead = opening?.RecordedHead ?? 0;
        _redactor = redactor;
        _reduction = reduction;
        _logger = logger;
    }

    /// <summary>Whether frames are actually being captured. False ⇒ the pump still parses exactly as before and records nothing.</summary>
    internal bool IsCapturing => _plane is not null && _handle is not null;

    /// <summary>Whether the captured frames are also being folded into a resumable reduction. False ⇒ frames are still captured and no checkpoint is written.</summary>
    internal bool IsReducing => _reduction.IsReducing;

    /// <summary>
    /// The execution-identity key for a harness, as <c>&lt;kind&gt;/v&lt;major&gt;</c>: the adapter's stable tag plus the
    /// major of the harness version it pins, so a row read a year later is interpretable against the adapter that wrote
    /// it. A version that names no leading number (a test double, an unpinned adapter) is v1 rather than unrepresentable.
    /// </summary>
    internal static string HarnessTypeKeyOf(IAgentHarness harness)
    {
        var digits = new string((harness.Version ?? string.Empty).TrimStart().TakeWhile(char.IsAsciiDigit).ToArray());
        var major = int.TryParse(digits, out var parsed) && parsed > 0 ? parsed : 1;

        return $"{harness.Kind.Trim().ToLowerInvariant()}/v{major}";
    }

    /// <summary>
    /// Opens a capture stream — a new process's, or the RESUMED stream of one already recorded when
    /// <see cref="NativeRecordCaptureRequest.Resume"/> is set — and resumes the execution's reduction behind it.
    /// Degrades to a parse-only pump when the plane is absent, cannot open, or has no live process to resume: the same
    /// shape the shadow log capture already uses, for the same reason, that a run must never depend on it. The
    /// redactor is the run's own, and the only thing that keeps a parser's exception message out of storage.
    /// </summary>
    internal static async Task<AgentNativeRecordPump> OpenAsync(INativeRecordPlane? plane, NativeRecordCaptureRequest request, SecretRedactor redactor, ILogger logger, CancellationToken cancellationToken)
    {
        if (plane is null) return Closed(redactor, logger);

        try
        {
            var opening = await OpenedAsync(plane, request, cancellationToken).ConfigureAwait(false);

            if (opening is null) return Closed(redactor, logger);

            var reduction = await HarnessReductionSink.OpenAsync(plane, opening.Handle, logger, cancellationToken).ConfigureAwait(false);

            return new AgentNativeRecordPump(plane, opening, redactor, reduction, logger);
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(exception, "Native record capture could not open for agent run {RunId}; the run streams unchanged with no native records", request.AgentRunId);

            return Closed(redactor, logger);
        }
    }

    /// <summary>
    /// A LAUNCH appends the next process of the execution and starts its stream's cursor at zero with nothing recorded
    /// behind it; a RESUME re-enters the process already recorded, starts at the position the observation resumes
    /// reading at, and learns how far that process's records already reach. A plane that cannot resume — it does not
    /// implement the sibling capability, or the run has no live recorded process — yields no opening, and the
    /// re-attach then streams with no native records rather than inventing a second process row for the one it is
    /// observing.
    /// </summary>
    private static async Task<NativeRecordCaptureOpening?> OpenedAsync(INativeRecordPlane plane, NativeRecordCaptureRequest request, CancellationToken cancellationToken)
    {
        if (request.Resume)
        {
            return plane is INativeRecordExecutionPlane executions
                ? await executions.ReopenAsync(request, cancellationToken).ConfigureAwait(false)
                : null;
        }

        var handle = await plane.OpenAsync(request, cancellationToken).ConfigureAwait(false);

        return handle is null ? null : new NativeRecordCaptureOpening { Handle = handle, SourceHead = 0 };
    }

    /// <summary>A pump that parses exactly as before and records nothing.</summary>
    private static AgentNativeRecordPump Closed(SecretRedactor redactor, ILogger logger) => new(null, null, redactor, HarnessReductionSink.Disabled(logger), logger);

    /// <summary>
    /// Capture one frame and then ask the harness what it is. The record is built from the REDACTED line, so no secret
    /// reaches storage, while its source geometry describes the RAW frame — which is what makes "how much did redaction
    /// change" a computable fact rather than a lost one. A parser that throws has its frame recorded with the reason
    /// and the throw then PROPAGATES: a frame we can no longer interpret is not a frame we lost, and recording that is
    /// the whole gain here — swallowing the throw on top of it would silently turn a run that used to fail into one
    /// that succeeds, and would do it differently depending on whether the plane is deployed.
    /// </summary>
    internal async Task<NativeFrame> CaptureAsync(string rawLine, string redactedLine, IAgentHarness harness, CancellationToken cancellationToken)
    {
        await FlushIfFullAsync(cancellationToken).ConfigureAwait(false);

        var frame = IsCapturing ? BuildFrame(rawLine, redactedLine, isFinal: true) : null;

        if (frame is not null) Ground(frame, harness);

        try
        {
            var parsed = harness.ParseEvents(rawLine);

            if (frame is not null) _records.Add(Captured(frame, parsed.Count > 0, null));

            return new NativeFrame(frame?.RecordId, parsed);
        }
        catch (Exception exception)
        {
            await RecordUnreadableAsync(frame, harness, exception, cancellationToken).ConfigureAwait(false);

            throw;
        }
    }

    /// <summary>
    /// Capture one DIAGNOSTIC line — a frame of the harness's own stderr — as a record, and ask no parser anything
    /// about it.
    ///
    /// <para><b>Why no parser.</b> A harness parser is written against that harness's stdout protocol. Feeding a
    /// diagnostic line to it would not merely waste a call: a warning that happens to look like a protocol frame would
    /// be normalized into a semantic event, and the projection would then carry as an event something the harness never
    /// said. So the record states <see cref="NativeRecordNormalization.NotParsed"/> — no parse ran, which is a
    /// different fact from a parse that found nothing.</para>
    ///
    /// <para><b>Why the reader's cut is carried, not smoothed over.</b> A diagnostic longer than the reader's own pass
    /// arrives cut — the alternative is a reader that stops at it for good. Recording the two halves as two final
    /// frames would put in the durable stream two diagnostics the harness never wrote, so
    /// <paramref name="isComplete"/> false is recorded as <see cref="NativeRecordV1.IsFinal"/> false: the reader that
    /// stops there is told it holds half a frame. It is also what keeps the cursor true — a cut line carried no
    /// terminator byte, so none is counted for it and its continuation opens at exactly the byte the reader resumes
    /// from.</para>
    ///
    /// <para>Everything else is the stdout path exactly: the payload is the REDACTED line, the geometry describes the
    /// raw one, the cursor advances per line and the ordinal per RECORDED line, and retention stays one buffered record
    /// per line with nothing surviving a flush. A frame below this opening's recorded head is one an earlier drain of
    /// the same process already recorded and is dropped rather than counted twice.</para>
    /// </summary>
    internal async Task CaptureDiagnosticAsync(string rawLine, string redactedLine, bool isComplete, CancellationToken cancellationToken)
    {
        await FlushIfFullAsync(cancellationToken).ConfigureAwait(false);

        if (!IsCapturing) return;

        if (BuildFrame(rawLine, redactedLine, isComplete) is { } frame) _records.Add(Diagnostic(frame));
    }

    /// <summary>Buffer the projection of one normalized event onto the frame it came from. Silently a no-op when the frame was never captured, so the caller's loop reads the same either way.</summary>
    internal void Project(NativeFrame frame, AgentEvent normalized)
    {
        if (_handle is not { } handle || frame.RecordId is not { } recordId) return;

        _events.Add(new AgentSemanticEventV1
        {
            ContractVersion = WorkflowRunDataContract.CurrentVersion,
            EventId = Guid.NewGuid(),
            EventType = EventTypeNamespace + Kebab(normalized.Kind.ToString()),
            EventSchemaVersion = 1,
            SourceNativeRecordIds = new[] { recordId },
            ExecutionId = handle.ExecutionId,

            // Ignorable, stated rather than assumed: nothing reads this plane yet and the normalized agent_run_event
            // log remains the authority, so a reader that cannot route one of these loses no fact it is accountable for.
            Necessity = SemanticEventNecessity.Ignorable,

            // Derived, and never Exact: ParseEvents NORMALIZES a frame (it maps a native kind onto the shared
            // vocabulary and renders a display line) rather than transcribing it, and a projection that claimed the
            // harness's own words for a normalization is exactly how a guessed fact gets audited as a stated one. The
            // projector that may honestly claim more is GroundedFrameProjector, which reads the harness's OWN
            // structured record out of the captured bytes rather than this normalization of them.
            ProjectionQuality = SemanticProjectionQuality.Derived,
        });
    }

    /// <summary>
    /// Buffer the EXACTLY GROUNDED projection of this frame, when the harness recognises its own structured session
    /// record in the captured bytes. It rides beside the derived projections of the same frame rather than replacing
    /// them: they are two readings of one frame at two fidelities, and the reduction takes its named facts only from
    /// the grounded one.
    ///
    /// <para>Asked BEFORE the parser and independently of it, so a frame whose parser then throws still contributes the
    /// fact its bytes stated — the fact was never the parser's to lose.</para>
    ///
    /// <para>A reader that throws is CONTAINED here, and that asymmetry with <see cref="CaptureAsync"/>'s parser is
    /// deliberate. The parser's throw already failed the run before this plane existed, so containing it would change
    /// what a run resolves to; reading a grounded fact is work that did not exist before, so letting it throw would
    /// make a run's outcome depend on whether this plane is deployed — the one thing a dual write may not do.</para>
    /// </summary>
    private void Ground(NativeRecordV1 record, IAgentHarness harness)
    {
        if (_handle is not { } handle) return;

        try
        {
            if (GroundedFrameProjector.Project(harness, handle, record) is { } projection) _events.Add(projection);
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Harness {Harness} could not read a grounded fact from a native frame; the frame is recorded unchanged, no exact projection is made, and the run is untouched", harness.Kind);
        }
    }

    /// <summary>Write everything buffered. A plane that will not accept a batch disables capture for the rest of the round rather than taking the run down with it. A worker tear-down is the one thing not contained here — that cancellation IS the round ending, and it belongs to the caller.</summary>
    internal async Task FlushAsync(CancellationToken cancellationToken)
    {
        if (_plane is null || _handle is not { } handle) return;
        if (_records.Count == 0 && _events.Count == 0) return;

        var batch = new NativeRecordBatch { Handle = handle, Records = _records.ToList(), Events = _events.ToList() };

        _records.Clear();
        _events.Clear();

        // Folded BEFORE the write, so a frame that becomes durable is already in the reduction, and the checkpoint it
        // produces then commits with that very batch — the window in which a durable frame is missing from the stored
        // prefix never opens.
        var checkpoint = _reduction.Reduce(batch);

        try
        {
            await PersistAsync(batch, checkpoint, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(exception, "Native record capture could not persist {RecordCount} frame(s) and {EventCount} projection(s) for agent run {RunId}; capture stops for this round and the run continues unchanged", batch.Records.Count, batch.Events.Count, handle.AgentRunId);

            _handle = null;
        }
    }

    /// <summary>One write either way. A batch with a checkpoint goes through the sibling capability so the two share a transaction; a reduction that never opened, or has already stopped, leaves the plain write exactly as it was.</summary>
    private async Task PersistAsync(NativeRecordBatch batch, HarnessReductionCheckpointV1? checkpoint, CancellationToken cancellationToken)
    {
        if (checkpoint is null || _plane is not INativeRecordReductionPlane reductions)
        {
            await _plane!.WriteAsync(batch, cancellationToken).ConfigureAwait(false);
            return;
        }

        await reductions.WriteReducedAsync(batch, checkpoint, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Flush, then record how this round's physical process ended. Best-effort on both halves — the Agent Run's own outcome is decided elsewhere and is not affected by either.</summary>
    internal async Task CloseAsync(int? exitCode, CancellationToken cancellationToken)
    {
        await FlushAsync(cancellationToken).ConfigureAwait(false);

        if (_plane is null || _handle is not { } handle) return;

        try
        {
            await _plane.CloseAsync(handle, exitCode, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(exception, "Native record capture could not close the process attempt for agent run {RunId}; the attempt stays open for a later sweep and the run completes unchanged", handle.AgentRunId);
        }
    }

    private async Task FlushIfFullAsync(CancellationToken cancellationToken)
    {
        if (_records.Count < MaxBuffered && _events.Count < MaxBuffered) return;

        await FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Records a frame its parser could not read, then flushes BEFORE the throw unwinds the round — the buffered batch
    /// gets no later chance to become durable, and a frame nobody can interpret that nobody can see either is exactly
    /// the hole this plane exists to end. The reason is REDACTED first: it is the one string here not derived from the
    /// already-redacted bytes, and a parser that echoes the line it choked on would otherwise put a secret in a column.
    /// The flush's own failure is swallowed so what the run sees is the PARSER's exception, byte-for-byte as before.
    /// <para>No frame ⇒ nothing to record: capture is off, or this line is one an earlier opening of the same process
    /// already recorded — together with what its parser made of it, which was this same failure.</para>
    /// </summary>
    private async Task RecordUnreadableAsync(NativeRecordV1? frame, IAgentHarness harness, Exception exception, CancellationToken cancellationToken)
    {
        if (frame is null) return;

        _logger.LogWarning(exception, "Harness {Harness} could not normalize a native frame; the frame is recorded normalization-failed and the parser's exception propagates unchanged", harness.Kind);

        _records.Add(Captured(frame, false, _redactor.Redact(exception.Message)));

        try
        {
            await FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception flushFailure)
        {
            _logger.LogWarning(flushFailure, "Native record capture could not flush the frame its parser threw on; the parser's own failure is what the run sees");
        }
    }

    /// <summary>
    /// The frame as the contract states it, or NULL for a line an earlier opening of this same process already
    /// recorded. Total: <see cref="NativeRecordV1.ByteOffset"/> and <see cref="NativeRecordV1.ByteLength"/> describe
    /// the RAW line while <see cref="NativeRecordV1.Digest"/> and <see cref="NativeRecordV1.SizeBytes"/> describe the
    /// captured (redacted) bytes, and <see cref="NativeRecordV1.Redaction"/> says which of the two the payload is — so
    /// a masked frame can never be read back as verbatim.
    ///
    /// <para>The cursor advances for EVERY line and the ordinal only for a recorded one: the cursor describes the
    /// source, which the skipped line occupies whether or not this opening records it, while the ordinal counts this
    /// stream's own frames and 0139 requires it contiguous.</para>
    ///
    /// <para>The cursor is reconstructed from the delivered lines plus the terminator byte a TERMINATED one carried.
    /// For the newline-terminated spool a runner writes that is the source's own byte offset, which is what lets the
    /// resume position and the recorded head be compared at all. A line the reader states it had to CUT carried no
    /// terminator and is counted without one, so its continuation opens exactly where it ended. Where the reader
    /// cannot preserve the accounting — a CR it trimmed from a CRLF ending, an unterminated final line it delivered as
    /// whole — the two drift, and the seam then carries a re-delivered line the head no longer covers. That is a
    /// duplicate the byte ranges show, not one they hide, and closing it needs the reader to state each line's true
    /// offset rather than this side to guess better.</para>
    /// </summary>
    private NativeRecordV1? BuildFrame(string rawLine, string redactedLine, bool isFinal)
    {
        var handle = _handle!;
        var captured = Encoding.UTF8.GetBytes(redactedLine);
        var sourceLength = Encoding.UTF8.GetByteCount(rawLine);
        var offset = _sourceOffset;

        // The line terminator the stream carried but the delivered line does not — a cut line carried none, so counting
        // one for it would put its own continuation a byte past where the reader will resume.
        _sourceOffset += sourceLength + (isFinal ? 1 : 0);

        if (offset < _recordedHead) return null;

        return new NativeRecordV1
        {
            ContractVersion = WorkflowRunDataContract.CurrentVersion,
            RecordId = Guid.NewGuid(),
            StreamId = handle.StreamId,
            Ordinal = _ordinal++,
            Channel = handle.Channel,
            NativeType = NativeTypeOf(redactedLine),
            IngestedAt = DateTimeOffset.UtcNow,
            ByteOffset = offset,
            ByteLength = sourceLength,
            InlinePayload = redactedLine,
            DigestAlgorithm = WorkflowRunDataContract.Sha256Algorithm,
            Digest = Convert.ToHexString(SHA256.HashData(captured)).ToLowerInvariant(),
            SizeBytes = captured.Length,
            Encoding = NativeRecordPayloadEncoding.Utf8,
            Redaction = string.Equals(rawLine, redactedLine, StringComparison.Ordinal) ? NativeRecordRedaction.None : NativeRecordRedaction.Masked,
            IsFinal = isFinal,
        };
    }

    private static NativeRecordCapture Captured(NativeRecordV1 frame, bool projected, string? failure) => new()
    {
        Frame = frame,
        Normalization = failure is not null ? NativeRecordNormalization.Failed
            : projected ? NativeRecordNormalization.Projected
            : NativeRecordNormalization.Unrecognized,
        NormalizationErrorCode = failure is null ? null : NormalizationThrewErrorCode,
        NormalizationErrorMessage = failure is null ? null : Clamp(failure, 2048),
    };

    private static NativeRecordCapture Diagnostic(NativeRecordV1 frame) => new()
    {
        Frame = frame,
        Normalization = NativeRecordNormalization.NotParsed,
    };

    /// <summary>
    /// The frame's own type name, read out of the captured bytes by a total rule with a fixed fallback — a LABEL, so
    /// that "which native frame classes are we failing to interpret" is answerable without re-reading every payload.
    /// It is derived, never stated by the harness, and nothing exact rests on it; the harness declaring its own frame
    /// types is the descriptor plane's business, not this one's.
    /// </summary>
    private static string NativeTypeOf(string payload)
    {
        if (payload.Length == 0 || payload[0] != '{') return UnstructuredNativeType;

        try
        {
            using var document = JsonDocument.Parse(payload);

            if (document.RootElement.ValueKind != JsonValueKind.Object) return UnstructuredNativeType;

            return Named(document.RootElement, "type") ?? Named(document.RootElement, "method") ?? UnstructuredNativeType;
        }
        catch (JsonException)
        {
            return UnstructuredNativeType;
        }
    }

    private static string? Named(JsonElement root, string property) =>
        root.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString())
            ? Clamp(value.GetString()!, 255)
            : null;

    private static string Clamp(string value, int maximum) => value.Length <= maximum ? value : value[..maximum];

    /// <summary>Renders a vocabulary member as the kebab segment of its event URI, so <c>AssistantMessage</c> is <c>assistant-message</c> on the wire regardless of how C# spells it.</summary>
    private static string Kebab(string name)
    {
        var builder = new StringBuilder(name.Length + 8);

        foreach (var character in name)
        {
            if (char.IsUpper(character) && builder.Length > 0) builder.Append('-');

            builder.Append(char.ToLowerInvariant(character));
        }

        return builder.ToString();
    }
}

/// <summary>One captured frame and what the harness made of it: the record id every projection of this frame cites (null when capture is off), and the events the parser yielded.</summary>
internal sealed record NativeFrame(Guid? RecordId, IReadOnlyList<AgentEvent> Events);
