using System.Text.Json.Serialization;

namespace CodeSpace.Messages.Dtos.Agents;

public sealed record AgentRunLogPage
{
    public required IReadOnlyList<AgentRunLogStreamSummary> Items { get; init; }
    public string? NextCursor { get; init; }
}

public sealed record AgentRunLogStreamSummary
{
    public required Guid StreamId { get; init; }
    public required Guid AgentRunId { get; init; }
    public required string StreamKind { get; init; }
    public required string ContentType { get; init; }
    public string? ContentEncoding { get; init; }
    public required string CaptureSource { get; init; }
    public required string Retention { get; init; }
    public required AgentRunLogStatus Status { get; init; }
    public required long Revision { get; init; }
    public required long SegmentCount { get; init; }
    public required long TotalBytes { get; init; }
    public string? Sha256 { get; init; }
    public AgentRunLogIntegrity? Integrity { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public required DateTimeOffset LastModifiedAt { get; init; }
    public DateTimeOffset? CompletedAt { get; init; }
    public string? ErrorCode { get; init; }
}

/// <summary>Historical complete segment-read verification. It does not assert point-in-time object availability or retention.</summary>
public sealed record AgentRunLogIntegrity
{
    public required string Kind { get; init; }
    public string? ManifestDigest { get; init; }
    public long? VerifiedSegmentCount { get; init; }
    public long? VerifiedBytes { get; init; }
    public DateTimeOffset? VerifiedAt { get; init; }
}

public enum AgentRunLogStatus
{
    Open,
    Completed,
    Truncated,
    Unavailable,
    Corrupt,
    CaptureFailed,
}

public sealed record AgentRunLogRangeRead
{
    public required AgentRunLogReadAvailability Availability { get; init; }
    public required AgentRunLogStreamSummary Stream { get; init; }
    public required long OffsetBytes { get; init; }
    public required long NextOffsetBytes { get; init; }
    public required bool HasMore { get; init; }
    public required bool IsRetryable { get; init; }
    public string? ProblemCode { get; init; }

    [JsonIgnore]
    public byte[] Content { get; init; } = [];
}

public enum AgentRunLogReadAvailability
{
    Available,
    InvalidRange,
    PhysicalObjectMissing,
    IntegrityFailure,
    BackendUnavailable,
    AccessDenied,
    ProviderTimeout,
    Unsupported,
}

public sealed record AgentRunLogReadProblem
{
    public required AgentRunLogReadAvailability Availability { get; init; }
    public required string Code { get; init; }
    public required bool IsRetryable { get; init; }
    public required Guid StreamId { get; init; }
}

/// <summary>
/// One statement that a run's log capture outlived the worker that owned it: the run reached a terminal state on some
/// OTHER host, and the streams its dead generation left Open can never be drained, completed or failed by the process
/// that was doing the capturing.
///
/// <para><paramref name="WorkerFenceEpoch"/> is the CALLER's own generation — the fence the abandon just minted, not
/// the one the orphaned streams carry. It is both the caller's authority (a superseded sweep whose epoch is no longer
/// the run's current one may state nothing) and the boundary of what may be flipped: only streams STRICTLY behind it
/// are orphaned, so a stream a live worker still owns is never touched. It is also the epoch every
/// <c>agent_run_cleanup_receipt</c> row of that same abandon is stamped with, which is what lets the stream's error
/// message cite the durable record of the abandon it belongs to.</para>
///
/// <para>Committed bytes are NOT part of this statement. The flip leaves the segment rows, the byte head and the
/// source offsets exactly where they were, so everything the dead worker did manage to make durable stays readable
/// through the ordinary read path — what ends is the pretence that more of it is still on the way.</para>
/// </summary>
public sealed record AgentRunLogOwnerLossRequest(Guid TeamId, Guid AgentRunId, long WorkerFenceEpoch, string ErrorCode)
{
    /// <summary>
    /// The code an orphaned capture is terminalized with. Pinned by a unit test: it is written into durable rows, read
    /// back by the Room and by operators, and a rename would silently orphan every stream already carrying the old one.
    /// </summary>
    public const string OwnerLostErrorCode = "capture.owner-lost";
}
