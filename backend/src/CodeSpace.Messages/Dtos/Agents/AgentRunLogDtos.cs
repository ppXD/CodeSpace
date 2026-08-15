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
    public required DateTimeOffset CreatedAt { get; init; }
    public required DateTimeOffset LastModifiedAt { get; init; }
    public DateTimeOffset? CompletedAt { get; init; }
    public string? ErrorCode { get; init; }
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
