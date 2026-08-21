using System.Text;
using System.Text.Json.Serialization;
using CodeSpace.Messages.Contracts;

namespace CodeSpace.Messages.Agents;

/// <summary>
/// WHICH of a harness's native streams a frame arrived on. A harness speaks several at once, and conflating them is
/// what makes a record lossy: a token-usage frame folded into "stdout" can no longer be read as exact model
/// telemetry, and a session-state blob folded into "stdout" is skipped the moment the line looks too big to be text.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum NativeRecordChannel
{
    /// <summary>The process's standard output.</summary>
    Stdout,

    /// <summary>The process's standard error, kept separate so a diagnostic is never parsed as a protocol frame.</summary>
    Stderr,

    /// <summary>The harness's own protocol channel (JSON-RPC messages, framed events) when it is not stdout.</summary>
    Protocol,

    /// <summary>Control traffic the CONTROLLER sent or received — steer, abort, approve — rather than the harness's narration.</summary>
    Control,

    /// <summary>The harness's own session state (transcript, rollout, resume blob), which is arbitrarily large and must never be treated as a log line.</summary>
    SessionState,

    /// <summary>The model request/response wire as the harness saw it — the only source that makes model telemetry exact.</summary>
    ModelWire,

    /// <summary>The tool invocation/result wire as the harness saw it — the only source that makes tool telemetry exact.</summary>
    ToolWire,

    /// <summary>Output produced by a harness hook rather than the harness itself.</summary>
    Hook,

    /// <summary>Metric or heartbeat frames.</summary>
    Metric,

    /// <summary>Harness debug/verbose output, retained but never a source for an exact fact.</summary>
    Debug,
}

/// <summary>How the referenced bytes relate to what actually crossed the wire. It travels WITH the record because a reader that cannot tell verbatim from masked cannot tell a missing secret from a missing fact.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum NativeRecordRedaction
{
    /// <summary>The referenced bytes are the native frame verbatim.</summary>
    None,

    /// <summary>Secret spans were replaced before capture: the structure survives, the bytes differ from the wire.</summary>
    Masked,

    /// <summary>The frame was deliberately not captured. Only this record's metadata survives, and the payload must be a reference to unavailable content — never inline.</summary>
    Withheld,
}

/// <summary>How the payload string encodes the captured bytes.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum NativeRecordPayloadEncoding
{
    /// <summary>The payload characters ARE the native text.</summary>
    Utf8,

    /// <summary>The payload is base64 of the raw bytes, used when the frame is not valid UTF-8.</summary>
    Base64,
}

/// <summary>
/// ONE losslessly captured native frame, exactly as a harness produced it. This is the floor of the data plane: a
/// semantic event is a PROJECTION of these, never a substitute, so anything a projection could not represent is
/// still recoverable from here. Persisted as <c>workflow_run_native_record</c>.
///
/// <para>Small frames ride inline; large ones ride as a <see cref="WorkflowRunArtifactRefV1"/> in whatever storage
/// the operator configured — exactly one of the two, always, so "no payload" can never be silently read as an empty
/// string.</para>
/// </summary>
public sealed record NativeRecordV1
{
    /// <summary>Data-contract version these fields are stamped with.</summary>
    public required int ContractVersion { get; init; }

    /// <summary>Identity of this frame, referenced by every semantic event projected from it.</summary>
    public required Guid RecordId { get; init; }

    /// <summary>The capture stream this frame belongs to — one stream per (execution, channel) opening.</summary>
    public required Guid StreamId { get; init; }

    /// <summary>Zero-based position WITHIN <see cref="StreamId"/>. Ordering is per stream, never global: a global sequence would force the channels to serialise through one writer.</summary>
    public required long Ordinal { get; init; }

    /// <summary>Which native stream this frame arrived on.</summary>
    public required NativeRecordChannel Channel { get; init; }

    /// <summary>The harness's OWN name for this frame (e.g. <c>assistant</c>, <c>token_count</c>), kept unnormalized so a frame the adapter did not understand is still classifiable later.</summary>
    public required string NativeType { get; init; }

    /// <summary>Identifier of the harness's schema for this frame, when it names one.</summary>
    public string? NativeSchema { get; init; }

    /// <summary>Version of <see cref="NativeSchema"/>, when the harness declares one.</summary>
    public string? NativeSchemaVersion { get; init; }

    /// <summary>When the harness says the frame happened. Null when it says nothing — never back-filled from ingestion, which would invent precision the harness never gave.</summary>
    public DateTimeOffset? OccurredAt { get; init; }

    /// <summary>When capture observed the frame. Always known, and the only clock the capture side controls.</summary>
    public required DateTimeOffset IngestedAt { get; init; }

    /// <summary>
    /// Start of the ORIGINAL frame within its stream. Durable stdout producers copy the source reader's exact start;
    /// legacy/non-durable producers retain their best-known cursor and leave <see cref="ByteEndOffset"/> null.
    /// <para>It is not a claim that the seam has no overlap. The re-attach is re-delivered every line recorded after
    /// that committed offset, and what keeps each source line recorded once is the writer dropping anything below the
    /// exact head its process already covers — not this field being contiguous.</para>
    /// </summary>
    public required long ByteOffset { get; init; }

    /// <summary>Byte length of the ORIGINAL frame in the stream. It differs from <see cref="SizeBytes"/> whenever the payload was masked or withheld, which is precisely how much was dropped.</summary>
    public required long ByteLength { get; init; }

    /// <summary>
    /// Exclusive end of this frame in the source reader's byte coordinates. Null only for a legacy producer that
    /// could state decoded content length but not the source terminator; durable stdout producers always set it.
    /// </summary>
    public long? ByteEndOffset { get; init; }

    /// <summary>The payload itself, for frames small enough to ride inline. Mutually exclusive with <see cref="PayloadRef"/>.</summary>
    public string? InlinePayload { get; init; }

    /// <summary>The payload in operator-configured storage, for frames too large to ride inline. Mutually exclusive with <see cref="InlinePayload"/>.</summary>
    public WorkflowRunArtifactRefV1? PayloadRef { get; init; }

    /// <summary>Digest algorithm of <see cref="Digest"/>.</summary>
    public required string DigestAlgorithm { get; init; }

    /// <summary>Digest of the CAPTURED payload bytes — the bytes <see cref="InlinePayload"/> decodes to, or the ones <see cref="PayloadRef"/> points at.</summary>
    public required string Digest { get; init; }

    /// <summary>Size in bytes of the captured payload.</summary>
    public required long SizeBytes { get; init; }

    /// <summary>How the payload encodes those bytes.</summary>
    public required NativeRecordPayloadEncoding Encoding { get; init; }

    /// <summary>How the captured bytes relate to the wire.</summary>
    public required NativeRecordRedaction Redaction { get; init; }

    /// <summary>Whether this record completes its native frame. False ⇒ a reader that stops here has half a frame and must know it: the rest is in the continuation record that follows, or — where a bounded capture stopped at the cut — still at the source.</summary>
    public required bool IsFinal { get; init; }

    /// <summary>Every reason this record cannot be trusted as a faithful capture. Empty ⇒ readable.</summary>
    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();

        if (!WorkflowRunDataContract.IsSupported(ContractVersion))
            errors.Add($"contractVersion '{ContractVersion}' is unsupported");
        if (RecordId == Guid.Empty)
            errors.Add("recordId must be non-empty");
        if (StreamId == Guid.Empty)
            errors.Add("streamId must be non-empty");
        if (Ordinal < 0)
            errors.Add("ordinal must be non-negative");
        if (!Enum.IsDefined(Channel))
            errors.Add($"channel '{Channel}' is unsupported");
        if (string.IsNullOrWhiteSpace(NativeType))
            errors.Add("nativeType must be non-empty");
        if (ByteOffset < 0 || ByteLength < 0)
            errors.Add("byteOffset and byteLength must be non-negative");
        if (ByteEndOffset is { } end && end < ByteOffset + ByteLength)
            errors.Add("byteEndOffset must cover the raw frame content");

        errors.AddRange(DigestErrors());
        errors.AddRange(PayloadErrors());
        errors.AddRange(RedactionErrors());

        return errors;
    }

    /// <summary>Prints the record's identity and payload BINDING, never the payload. <see cref="InlinePayload"/> can be a <see cref="NativeRecordChannel.ModelWire"/> frame — the model request wire, headers included — which is exactly why <see cref="Redaction"/> exists; the generated record printout would put all of it in the first log line that formats a record.</summary>
    private bool PrintMembers(StringBuilder builder)
    {
        builder.Append($"ContractVersion = {ContractVersion}, RecordId = {RecordId}, StreamId = {StreamId}, Ordinal = {Ordinal}, ");
        builder.Append($"Channel = {Channel}, NativeType = {NativeType}, NativeSchema = {NativeSchema}, NativeSchemaVersion = {NativeSchemaVersion}, ");
        builder.Append($"OccurredAt = {OccurredAt:O}, IngestedAt = {IngestedAt:O}, ByteOffset = {ByteOffset}, ByteLength = {ByteLength}, ByteEndOffset = {ByteEndOffset}, ");
        builder.Append($"InlinePayloadLength = {InlinePayload?.Length}, PayloadRef = {PayloadRef}, ");
        builder.Append($"DigestAlgorithm = {DigestAlgorithm}, Digest = {Digest}, SizeBytes = {SizeBytes}, ");
        builder.Append($"Encoding = {Encoding}, Redaction = {Redaction}, IsFinal = {IsFinal}");

        return true;
    }

    private IEnumerable<string> DigestErrors()
    {
        if (!string.Equals(DigestAlgorithm, WorkflowRunDataContract.Sha256Algorithm, StringComparison.Ordinal))
            yield return $"digestAlgorithm '{DigestAlgorithm}' is unsupported";
        if (!WorkflowRunDataContract.IsCanonicalSha256(Digest))
            yield return "digest must be a canonical lowercase SHA-256 value";
        if (SizeBytes < 0)
            yield return "sizeBytes must be non-negative";
    }

    private IEnumerable<string> PayloadErrors()
    {
        if (InlinePayload is null == PayloadRef is null)
            yield return "exactly one of inlinePayload and payloadRef must be present";
        if (!Enum.IsDefined(Encoding))
            yield return $"encoding '{Encoding}' is unsupported";

        if (PayloadRef is null) yield break;

        foreach (var error in PayloadRef.Validate()) yield return $"payloadRef: {error}";

        if (!string.Equals(PayloadRef.Digest, Digest, StringComparison.Ordinal) || PayloadRef.SizeBytes != SizeBytes)
            yield return "payloadRef must agree with the record's digest and sizeBytes";
    }

    /// <summary>
    /// The redaction and the payload's own completeness claim must AGREE. They are two statements about the same
    /// bytes made by two writers, and a reader that trusts the second one — <see cref="WorkflowRunCaptureCompleteness"/>
    /// promises only exact states enter a strict agent, resume, oracle or completion read — would take a frame that
    /// was masked, or never captured at all, as the frame the harness emitted. An unrecognised
    /// <see cref="Redaction"/> is itself an error rather than a value the rules below quietly skip over.
    /// </summary>
    private IEnumerable<string> RedactionErrors()
    {
        if (!Enum.IsDefined(Redaction))
            yield return $"redaction '{Redaction}' is unsupported";
        if (Redaction == NativeRecordRedaction.Withheld && InlinePayload is not null)
            yield return "a withheld payload must be a reference to unavailable content, never inline bytes";

        if (PayloadRef is null) yield break;

        if (Redaction == NativeRecordRedaction.Withheld && PayloadRef.Completeness != WorkflowRunCaptureCompleteness.Unavailable)
            yield return $"a withheld payload must reference unavailable content, not completeness '{PayloadRef.Completeness}'";
        if (Redaction == NativeRecordRedaction.Masked && PayloadRef.Completeness == WorkflowRunCaptureCompleteness.Exact)
            yield return "a masked payload cannot claim exact completeness; redactedExact is the strongest claim masked bytes support";
    }
}
