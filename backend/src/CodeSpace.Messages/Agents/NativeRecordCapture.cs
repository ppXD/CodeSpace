using System.Text.Json.Serialization;

namespace CodeSpace.Messages.Agents;

/// <summary>
/// What the harness's parser made of a captured frame. It travels ON the record because the alternative — inferring it
/// from whether any semantic event happens to cite the record — cannot tell "the parser had nothing to say" from "the
/// parser threw" from "the projection was written and lost", and those are three different defects.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum NativeRecordNormalization
{
    /// <summary>The parser yielded at least one event, and every one of them cites this record.</summary>
    Projected,

    /// <summary>The parser yielded nothing. This is the silent drop made durable and countable: today an unrecognised native frame class leaves no trace at all, so nobody can ask which classes are being lost.</summary>
    Unrecognized,

    /// <summary>
    /// NO parser was asked. The frame arrived on a channel this plane deliberately does not interpret —
    /// <see cref="NativeRecordChannel.Stderr"/>, whose diagnostics a parser built for the harness's stdout protocol
    /// would mis-read as protocol events and project into the semantic stream as facts nobody said.
    ///
    /// <para>Distinct from <see cref="Unrecognized"/> on purpose: that state means the parser had nothing to say, and
    /// recording it here would assert a parse that never ran and would fill "which frames could we not interpret" with
    /// every diagnostic line the run ever wrote.</para>
    /// </summary>
    NotParsed,

    /// <summary>The parser threw. The frame is still here and the redacted reason is recorded — losing the ability to interpret a frame is not losing the frame. The throw itself is NOT contained: it propagates into the run exactly as it did before this plane existed, so capture records the failure without deciding it.</summary>
    Failed,
}

/// <summary>What a capture opening needs in order to attach itself to a durable harness execution and physical process.</summary>
public sealed record NativeRecordCaptureRequest
{
    public required Guid TeamId { get; init; }
    public required Guid AgentRunId { get; init; }

    /// <summary>The adapter identity that is running, as <c>&lt;kind&gt;/v&lt;major&gt;</c> — snapshotted onto the execution so a row read a year later is interpretable against the adapter that produced it.</summary>
    public required string HarnessTypeKey { get; init; }

    /// <summary>Runner backend that owns the process, and the only interpreter of <see cref="RunnerLocatorJson"/>.</summary>
    public required string RunnerKind { get; init; }

    /// <summary>Backend-owned opaque address for this process. Never interpreted by shared code.</summary>
    public required string RunnerLocatorJson { get; init; }

    /// <summary>This worker's Agent Run fence. A stale value is REFUSED by the database rather than admitted, which is the intended outcome: a superseded worker must not append a process to a run it no longer owns.</summary>
    public required long WorkerFenceEpoch { get; init; }

    /// <summary>Which native stream this opening captures. One opening, one stream — a second channel is a second opening, never a second meaning for one stream id.</summary>
    public required NativeRecordChannel Channel { get; init; }

    /// <summary>
    /// Whether this opening RESUMES the observation of a process that is already recorded — a re-attach after a
    /// worker replacement — rather than launching a new one. A resumed opening appends NO process row, because the
    /// process it observes already has one; it mints its own stream and starts that stream's cursor at
    /// <see cref="ResumeSourceOffset"/>. The fields describing a LAUNCH
    /// (<see cref="HarnessTypeKey"/>, <see cref="RunnerKind"/>, <see cref="RunnerLocatorJson"/>,
    /// <see cref="WorkerFenceEpoch"/>) are not read on a resumed opening — nothing is written that could carry them.
    /// </summary>
    public bool Resume { get; init; }

    /// <summary>
    /// Where a RESUMED observation restarts reading its source, in the coordinates
    /// <see cref="NativeRecordV1.ByteOffset"/> is stated in. Zero on a launch, which reads from the beginning.
    ///
    /// <para>It is the position the observation ACTUALLY resumes at, and deliberately not the head of what is already
    /// recorded, because those two can differ: a frame is made durable by its batch write while the resume position is
    /// persisted afterwards, so the records can run ahead and the span between them is re-delivered. Starting the
    /// cursor here is what makes a re-delivered line land on the ground its first record already described, instead of
    /// on invented ground past that head where nothing could tell it apart from a line the process really emitted.</para>
    /// </summary>
    public long ResumeSourceOffset { get; init; }
}

/// <summary>
/// What a capture opening actually got: the durable identity it writes against, the SOURCE CURSOR its first frame is
/// recorded at, and how far this process's frames are already recorded.
///
/// <para>The last two are separate values because they can disagree, and the disagreement is the whole seam. A
/// re-attach resumes reading at <see cref="SourceHead"/>, which the write ordering keeps at or behind
/// <see cref="RecordedHead"/>; the span between them is re-delivered to the resumed observation. Recording at
/// <see cref="SourceHead"/> keeps every frame's position the position the source really has, and a line below
/// <see cref="RecordedHead"/> is one an earlier opening already recorded — dropped rather than recorded a second time,
/// because the fold counts a record and chains its digest and would do both twice.</para>
/// </summary>
public sealed record NativeRecordCaptureOpening
{
    public required NativeRecordCaptureHandle Handle { get; init; }

    /// <summary>Source cursor this opening's first frame is recorded at: zero for an opening that starts a process's stream, and the position the observation resumes reading at for one that continues it.</summary>
    public required long SourceHead { get; init; }

    /// <summary>How far this process's frames on this channel are ALREADY recorded, in the same coordinates. Zero for a launch, which has nothing behind it.</summary>
    public long RecordedHead { get; init; }
}

/// <summary>The durable identity a capture opening writes against: the execution it belongs to, the physical process inside it, and the stream its ordinals count within.</summary>
public sealed record NativeRecordCaptureHandle
{
    public required Guid TeamId { get; init; }
    public required Guid AgentRunId { get; init; }
    public required Guid ExecutionId { get; init; }
    public required Guid AttemptId { get; init; }
    public required Guid StreamId { get; init; }
    public required NativeRecordChannel Channel { get; init; }

    /// <summary>
    /// The workflow run this opening's Agent Run executes for, or NULL for a standalone run. Soft link, exactly as
    /// <c>AgentRun.WorkflowRunId</c> is, and read off the run rather than accepted from a caller that could disagree.
    ///
    /// <para><b>Why it rides the handle at all, and why it is <c>required</c> rather than defaulted.</b> A projection
    /// that names a row of a RUN-KEYED plane may only be minted where a workflow run exists to key it to — a
    /// standalone run's calls have no such row, and an id nothing holds a row for is worse than no id, because a reader
    /// joins on it and reads the miss as a data gap rather than as an absence. Every opening therefore has to STATE
    /// this, so a writer that forgot it is a compile error instead of a silent null that quietly disables the
    /// projection.</para>
    /// </summary>
    public required Guid? WorkflowRunId { get; init; }
}

/// <summary>
/// One captured frame plus the marker the contract record has no field for. <see cref="Frame"/> is the wire shape
/// itself, so the plane validates a capture against the contract BEFORE it persists one — rather than discovering at
/// read time that the two drifted. A capture that fails that check is a WRITER bug: the plane refuses the batch, the
/// pump above it stops capture for the round, and the run is untouched.
/// </summary>
public sealed record NativeRecordCapture
{
    public required NativeRecordV1 Frame { get; init; }
    public required NativeRecordNormalization Normalization { get; init; }

    /// <summary>Required by <see cref="NativeRecordNormalization.Failed"/> and refused by every other state, so "the parser threw" can never be recorded without a reason.</summary>
    public string? NormalizationErrorCode { get; init; }

    public string? NormalizationErrorMessage { get; init; }
}

/// <summary>
/// One BATCH of captured frames and everything projected from them — the semantic events, and the model calls the
/// harness's own records state — written in a single transaction so a projection can never outlive the frame it cites.
/// Batched for the same reason the normalized event writer is: one round trip per line would put a database write in
/// the middle of the harness's output loop.
/// </summary>
public sealed record NativeRecordBatch
{
    public required NativeRecordCaptureHandle Handle { get; init; }
    public required IReadOnlyList<NativeRecordCapture> Records { get; init; }
    public required IReadOnlyList<AgentSemanticEventV1> Events { get; init; }

    /// <summary>
    /// The model calls the harness's own records in this batch state, projected into the shape the model-call plane
    /// takes. Empty for a harness that prints no per-call record, and for every frame that is not one. It rides the same
    /// batch rather than opening a write of its own because a call and the frame that evidences it become durable
    /// together or not at all.
    /// </summary>
    public IReadOnlyList<HarnessModelCallProjectionV1> ModelCalls { get; init; } = Array.Empty<HarnessModelCallProjectionV1>();
}
