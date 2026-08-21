using System.Text.Json.Serialization;

namespace CodeSpace.Messages.Tasks.Trace;

/// <summary>
/// One exact, bounded UTF-8 byte range from PostgreSQL's canonical text representation of a Workflow Run record's
/// stored JSONB payload. This matches the existing Npgsql string representation; it does not claim to preserve logger
/// whitespace or object-property order that JSONB already normalized at insertion.
/// </summary>
public sealed record RunRecordPayloadRangeRead
{
    public required Guid RunId { get; init; }
    public required Guid RecordId { get; init; }
    public required long Sequence { get; init; }
    public required RunRecordPayloadReadAvailability Availability { get; init; }
    public required long OffsetBytes { get; init; }
    public required int ReturnedBytes { get; init; }
    public long? TotalBytes { get; init; }
    public long? NextOffsetBytes { get; init; }
    public string? ContentType { get; init; }
    public required bool IsRetryable { get; init; }
    public string? ProblemCode { get; init; }

    [JsonIgnore]
    public byte[] Content { get; init; } = [];
}

public enum RunRecordPayloadReadAvailability
{
    Available,
    InvalidRange,
}

public sealed record RunRecordPayloadReadProblem
{
    public required Guid RunId { get; init; }
    public required Guid RecordId { get; init; }
    public required long Sequence { get; init; }
    public required RunRecordPayloadReadAvailability Availability { get; init; }
    public required string Code { get; init; }
    public required bool IsRetryable { get; init; }
}
