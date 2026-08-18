using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
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
/// <para><b>Retention is O(1) in the event count.</b> The buffer is capped and flushed, exactly as
/// <c>BufferedEventWriter</c> is, because a long run must not be able to exhaust the heap here — the two accumulators
/// #1479 and #1489 removed are not to be replaced by a third. Per line the pump holds one record and its events, and
/// nothing that survives a flush.</para>
///
/// <para><b>It cannot change what the run resolves to.</b> A plane that will not open, or a write that will not land,
/// disables capture for the round with a warning; the harness's own output path is untouched, and nothing reads these
/// tables yet. Nor does capture SWALLOW anything: a parser that throws has its frame recorded with the reason and the
/// throw then propagates exactly as it did before this plane existed. Containing it here would have made the run's
/// outcome depend on whether a shadow plane happened to be deployed, which is the one thing a dual write may not do.</para>
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
    private readonly ILogger _logger;
    private readonly List<NativeRecordCapture> _records = new();
    private readonly List<AgentSemanticEventV1> _events = new();

    private NativeRecordCaptureHandle? _handle;
    private long _ordinal;
    private long _sourceOffset;

    private AgentNativeRecordPump(INativeRecordPlane? plane, NativeRecordCaptureHandle? handle, SecretRedactor redactor, ILogger logger)
    {
        _plane = plane;
        _handle = handle;
        _redactor = redactor;
        _logger = logger;
    }

    /// <summary>Whether frames are actually being captured. False ⇒ the pump still parses exactly as before and records nothing.</summary>
    internal bool IsCapturing => _plane is not null && _handle is not null;

    /// <summary>A pump that parses and records nothing — for a path where capture is deliberately not wired, so the absence is a named decision at the call site rather than a null nobody explains.</summary>
    internal static AgentNativeRecordPump Disabled(ILogger logger) => new(null, null, SecretRedactor.None, logger);

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

    /// <summary>Opens a capture stream, degrading to a parse-only pump when the plane is absent or will not open — the same shape the shadow log capture already uses, for the same reason: a run must never depend on it. The redactor is the run's own, and the only thing that keeps a parser's exception message out of storage.</summary>
    internal static async Task<AgentNativeRecordPump> OpenAsync(INativeRecordPlane? plane, NativeRecordCaptureRequest request, SecretRedactor redactor, ILogger logger, CancellationToken cancellationToken)
    {
        if (plane is null) return new AgentNativeRecordPump(null, null, redactor, logger);

        try
        {
            var handle = await plane.OpenAsync(request, cancellationToken).ConfigureAwait(false);

            return new AgentNativeRecordPump(handle is null ? null : plane, handle, redactor, logger);
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(exception, "Native record capture could not open for agent run {RunId}; the run streams unchanged with no native records", request.AgentRunId);

            return new AgentNativeRecordPump(null, null, redactor, logger);
        }
    }

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

        var frame = IsCapturing ? BuildFrame(rawLine, redactedLine) : null;

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
            // projector that may honestly claim more is the exact-telemetry one reading ModelWire/ToolWire frames.
            ProjectionQuality = SemanticProjectionQuality.Derived,
        });
    }

    /// <summary>Write everything buffered. A plane that will not accept a batch disables capture for the rest of the round rather than taking the run down with it. A worker tear-down is the one thing not contained here — that cancellation IS the round ending, and it belongs to the caller.</summary>
    internal async Task FlushAsync(CancellationToken cancellationToken)
    {
        if (_plane is null || _handle is not { } handle) return;
        if (_records.Count == 0 && _events.Count == 0) return;

        var batch = new NativeRecordBatch { Handle = handle, Records = _records.ToList(), Events = _events.ToList() };

        _records.Clear();
        _events.Clear();

        try
        {
            await _plane.WriteAsync(batch, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(exception, "Native record capture could not persist {RecordCount} frame(s) and {EventCount} projection(s) for agent run {RunId}; capture stops for this round and the run continues unchanged", batch.Records.Count, batch.Events.Count, handle.AgentRunId);

            _handle = null;
        }
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
    /// The frame as the contract states it. Pure and total: <see cref="NativeRecordV1.ByteOffset"/> and
    /// <see cref="NativeRecordV1.ByteLength"/> describe the RAW line while <see cref="NativeRecordV1.Digest"/> and
    /// <see cref="NativeRecordV1.SizeBytes"/> describe the captured (redacted) bytes, and <see cref="NativeRecordV1.Redaction"/>
    /// says which of the two the payload is — so a masked frame can never be read back as verbatim.
    /// </summary>
    private NativeRecordV1 BuildFrame(string rawLine, string redactedLine)
    {
        var handle = _handle!;
        var captured = Encoding.UTF8.GetBytes(redactedLine);
        var sourceLength = Encoding.UTF8.GetByteCount(rawLine);
        var offset = _sourceOffset;

        // The line terminator the stream carried but the delivered line does not. A final partial line without one
        // leaves the cursor one byte long, which is why this is a per-stream cursor and never a resume offset.
        _sourceOffset += sourceLength + 1;

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
            IsFinal = true,
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
