using CodeSpace.Messages.Agents;

namespace CodeSpace.Core.Persistence.Entities;

/// <summary>
/// ONE captured native frame of a harness process — the floor of the harness data plane. The normalized
/// <c>agent_run_event</c> log is an INTERPRETATION and is lossy by construction: <c>IAgentHarness.ParseEvents</c>
/// returns an empty list for any line it does not recognise, so a native frame class the adapter never learned leaves
/// no row at all. This row is what survives that: the frame is durable whatever the parser then makes of it.
///
/// <para><b>What "lossless" does and does not claim.</b> Not byte-for-byte native fidelity — the payload is the
/// REDACTED frame, because unredacted secrets must never reach storage. Following 0133's discipline,
/// <see cref="SourceOffsetBytes"/>/<see cref="SourceLengthBytes"/> describe the RAW frame's geometry in its stream
/// while <see cref="Digest"/>/<see cref="SizeBytes"/> describe the CAPTURED bytes, and <see cref="Redaction"/> states
/// which of the two the payload is — so how much redaction changed is computable rather than lost. What IS claimed:
/// every frame handed to capture gets a row, and the row's payload is never edited afterwards.</para>
///
/// <para><b>Append-only.</b> The database refuses every UPDATE and DELETE. <see cref="Normalization"/> is therefore
/// decided at insert, which is the point: a parse failure cannot be papered over by rewriting the frame to match a
/// later reading, and a re-interpretation is a new <see cref="WorkflowRunSemanticEvent"/> citing the same record.</para>
///
/// <para>Ordering is per <see cref="StreamId"/> and contiguous from zero, never global — one global sequence would
/// force every channel of a harness to serialise through a single writer.</para>
/// </summary>
public sealed class WorkflowRunNativeRecord : IEntity<Guid>
{
    public Guid Id { get; set; }
    public Guid TeamId { get; set; }
    public Guid AgentRunId { get; set; }
    public Guid ExecutionId { get; set; }

    /// <summary>The physical process that produced this frame. Soft correlation proved by the guard: the attempt table exposes no tenant-scoped key to reference, and widening another slice's table as a rider is not this one's business.</summary>
    public Guid AttemptId { get; set; }

    /// <summary>The capture stream this frame belongs to — one stream per (execution, channel) OPENING, so a re-attach's stream is its own and never renumbers the prefix.</summary>
    public Guid StreamId { get; set; }

    /// <summary>Zero-based position within <see cref="StreamId"/>, contiguous. A gap is an unrecorded frame, which is what this plane exists to make impossible.</summary>
    public long Ordinal { get; set; }

    public NativeRecordChannel Channel { get; set; } = NativeRecordChannel.Stdout;

    /// <summary>The frame's own type name, kept unnormalized so a frame the adapter did not understand is still classifiable later. Derived from the captured bytes by a total, non-throwing rule — it is a LABEL, and no exactness claim rests on it.</summary>
    public string NativeType { get; set; } = string.Empty;

    public string? NativeSchema { get; set; }
    public string? NativeSchemaVersion { get; set; }

    /// <summary>When the harness says the frame happened. Null when it says nothing — never back-filled from ingestion, which would invent precision the harness never gave.</summary>
    public DateTimeOffset? OccurredAt { get; set; }

    /// <summary>When capture observed the frame — the only clock the capture side controls.</summary>
    public DateTimeOffset IngestedAt { get; set; }

    /// <summary>Offset of the RAW frame within its stream. A per-stream cursor derived from the frames as delivered, each counted as its bytes plus one terminator — the source's own offsets for the newline-terminated stream a runner spools, and not otherwise a byte-exact index into one. Resuming the TAIL reads the log-capture plane's committed source head and never this, while a resumed CAPTURE starts its own stream at that same committed offset so both sides of a re-attach state their positions in one coordinate. Not a no-overlap guarantee — the re-attach is re-delivered every line recorded past that offset, and what keeps each source line recorded once is the writer refusing anything below the head its process already covers.</summary>
    public long SourceOffsetBytes { get; set; }

    /// <summary>Byte length of the RAW frame. It differs from <see cref="SizeBytes"/> exactly when redaction changed the bytes, which is how much redaction cost.</summary>
    public long SourceLengthBytes { get; set; }

    /// <summary>The captured payload, for a frame small enough to ride inline. Mutually exclusive with <see cref="PayloadRefJson"/> — never both, never neither, so an absent payload can never be read as an empty frame.</summary>
    public string? InlinePayload { get; set; }

    /// <summary>The captured payload in operator-configured storage, for a frame too large to ride inline. Written whenever a capture carries the ref arm; no capture PRODUCES one yet — the arbitrarily large channels (session state, model wire) are what a later slice points here.</summary>
    public string? PayloadRefJson { get; set; }

    public string DigestAlgorithm { get; set; } = string.Empty;

    /// <summary>Digest of the CAPTURED payload bytes — the bytes <see cref="InlinePayload"/> decodes to, not the raw frame's.</summary>
    public string Digest { get; set; } = string.Empty;

    /// <summary>Size of the captured payload in bytes.</summary>
    public long SizeBytes { get; set; }

    public NativeRecordPayloadEncoding PayloadEncoding { get; set; } = NativeRecordPayloadEncoding.Utf8;
    public NativeRecordRedaction Redaction { get; set; } = NativeRecordRedaction.None;

    /// <summary>Whether this record completes its native frame. False ⇒ a continuation record follows, so a reader that stops here has half a frame and must know it.</summary>
    public bool IsFinal { get; set; } = true;

    public NativeRecordNormalization Normalization { get; set; } = NativeRecordNormalization.Unrecognized;

    /// <summary>Why normalization failed. Required by <see cref="NativeRecordNormalization.Failed"/> and refused by every other state, so "the parser threw" can never be recorded without a reason.</summary>
    public string? NormalizationErrorCode { get; set; }

    public string? NormalizationErrorMessage { get; set; }
    public int ContractVersion { get; set; }
    public DateTimeOffset CreatedAt { get; set; }

    public WorkflowRunHarnessExecution Execution { get; set; } = default!;
}

/// <summary>
/// The PROJECTION of one or more <see cref="WorkflowRunNativeRecord"/>s into the normalized vocabulary the rest of the
/// system reads. It never replaces its sources: <see cref="SourceNativeRecordIds"/> keeps the exact frames it was
/// folded from and <see cref="ProjectionQuality"/> keeps how faithfully, so a later reader can always ask "did the
/// harness say this, or did we work it out?" and get a truthful answer.
///
/// <para>Append-only, like the records it cites — a projection that changed its mind is a new event over the same
/// frames, so the old reading stays auditable.</para>
/// </summary>
public sealed class WorkflowRunSemanticEvent : IEntity<Guid>
{
    public Guid Id { get; set; }
    public Guid TeamId { get; set; }
    public Guid AgentRunId { get; set; }
    public Guid ExecutionId { get; set; }

    /// <summary>The native records this event was folded from, in source order. At least ONE, always — stricter than <c>AgentSemanticEventV1</c>, which tolerates an ungrounded non-exact event; in this plane every event is a projection of a frame, so zero sources is never honest.</summary>
    public Guid[] SourceNativeRecordIds { get; set; } = Array.Empty<Guid>();

    /// <summary>Absolute URI naming what happened — a URI rather than a bare word so a harness-specific or operator-defined event cannot collide with a first-party one.</summary>
    public string EventType { get; set; } = string.Empty;

    /// <summary>One-based version of the payload schema for this <see cref="EventType"/>, independent of <see cref="ContractVersion"/>: an event type evolves without reversioning the whole plane.</summary>
    public int EventSchemaVersion { get; set; } = 1;

    public Guid? SessionId { get; set; }
    public Guid? TurnId { get; set; }
    public Guid? StepId { get; set; }
    public Guid? ModelCallId { get; set; }
    public Guid? ToolCallId { get; set; }
    public Guid? CorrelationId { get; set; }
    public Guid? CausationId { get; set; }

    public SemanticEventNecessity Necessity { get; set; } = SemanticEventNecessity.Ignorable;

    /// <summary>How faithfully this event represents its sources. The guard refuses an exactly-grounded quality over sources that were masked or never captured, because exactness is a claim about bytes and cannot outrun the bytes actually captured.</summary>
    public SemanticProjectionQuality ProjectionQuality { get; set; } = SemanticProjectionQuality.Unknown;

    public string? PayloadRefJson { get; set; }
    public int ContractVersion { get; set; }
    public DateTimeOffset ProjectedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }

    public WorkflowRunHarnessExecution Execution { get; set; } = default!;
}
