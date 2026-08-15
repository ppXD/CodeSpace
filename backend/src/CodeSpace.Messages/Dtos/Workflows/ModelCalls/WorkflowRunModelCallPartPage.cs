namespace CodeSpace.Messages.Dtos.Workflows.ModelCalls;

/// <summary>A bounded UTF-8 page of one Workflow Run model-call part.</summary>
public sealed record WorkflowRunModelCallPartPage
{
    public required WorkflowRunModelCallPart Part { get; init; }
    public required WorkflowRunModelCallPartAvailability Availability { get; init; }
    public string? Text { get; init; }
    public required long OffsetBytes { get; init; }
    public required int ReturnedBytes { get; init; }
    public long? TotalBytes { get; init; }
    public long? NextOffsetBytes { get; init; }
    public string? ContentType { get; init; }
    public Guid? ArtifactId { get; init; }
    public bool IntegrityVerified { get; init; }
    public string? Message { get; init; }
}

public enum WorkflowRunModelCallPartAvailability
{
    Available = 0,
    NotRecorded = 1,
    MetadataMissing = 2,
    PhysicalObjectMissing = 3,
    IntegrityFailure = 4,
    BackendUnavailable = 5,
    AccessDenied = 6,
    InvalidOffset = 7,
}
