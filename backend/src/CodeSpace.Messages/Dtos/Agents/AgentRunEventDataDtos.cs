using System.Text.Json.Serialization;

namespace CodeSpace.Messages.Dtos.Agents;

/// <summary>
/// One bounded byte range from the already-redacted, harness-native structured payload of one exact Agent Run event.
/// This is an event carrier, not a canonical tool-argument/result contract.
/// </summary>
public sealed record AgentRunEventDataRangeRead
{
    public required Guid AgentRunId { get; init; }
    public required long EventSequence { get; init; }
    public Guid? DataArtifactId { get; init; }
    public required AgentRunEventDataReadAvailability Availability { get; init; }
    public required long OffsetBytes { get; init; }
    public required int ReturnedBytes { get; init; }
    public long? TotalBytes { get; init; }
    public long? NextOffsetBytes { get; init; }
    public string? Sha256 { get; init; }
    public string? ContentType { get; init; }
    public required bool IntegrityVerified { get; init; }
    public required bool IsRetryable { get; init; }
    public string? ProblemCode { get; init; }

    [JsonIgnore]
    public byte[] Content { get; init; } = [];
}

public enum AgentRunEventDataReadAvailability
{
    Available,
    NotReferenced,
    InvalidRange,
    MetadataMissing,
    PhysicalObjectMissing,
    IntegrityFailure,
    BackendUnavailable,
    AccessDenied,
}

public sealed record AgentRunEventDataReadProblem
{
    public required Guid AgentRunId { get; init; }
    public required long EventSequence { get; init; }
    public Guid? DataArtifactId { get; init; }
    public required AgentRunEventDataReadAvailability Availability { get; init; }
    public required string Code { get; init; }
    public required bool IsRetryable { get; init; }
}
