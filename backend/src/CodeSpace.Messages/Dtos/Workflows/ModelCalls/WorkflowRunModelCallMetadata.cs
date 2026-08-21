using CodeSpace.Messages.Contracts;

namespace CodeSpace.Messages.Dtos.Workflows.ModelCalls;

/// <summary>
/// Metadata-only projection of one model call owned by a Workflow Run. It never contains prompt/output bytes; callers
/// fetch one bounded <see cref="WorkflowRunModelCallPart"/> page only when the operator opens that part.
/// </summary>
public sealed record WorkflowRunModelCallMetadata
{
    public required Guid RunId { get; init; }
    public required long Sequence { get; init; }
    /// <summary>The stable projected identity when admission has caught up; null only for an explicit legacy fallback.</summary>
    public Guid? WorkflowRunModelCallId { get; init; }
    public required WorkflowRunModelCallProjectionState ProjectionState { get; init; }
    public required WorkflowRunCaptureCompleteness CaptureCompleteness { get; init; }
    public Guid? CorrelationId { get; init; }
    public required WorkflowRunModelCallStatus Status { get; init; }
    public required IReadOnlyList<WorkflowRunModelCallPartDescriptor> Parts { get; init; }
}

public enum WorkflowRunModelCallProjectionState
{
    Projected = 0,
    LegacyFallback = 1,
}

public enum WorkflowRunModelCallStatus
{
    Completed = 0,
    Failed = 1,
}

/// <summary>Openable logical sections of the current interaction-ledger model-call representation.</summary>
public enum WorkflowRunModelCallPart
{
    Result = 0,
    SystemPrompt = 1,
    UserPrompt = 2,
    Usage = 3,
    Trace = 4,
    Error = 5,
}

public enum WorkflowRunModelCallPartSource
{
    NotRecorded = 0,
    Inline = 1,
    Artifact = 2,
    Synthesized = 3,
}

/// <summary>A byte-free descriptor. <see cref="SizeBytes"/> is recorded/derived size, not proof the blob is readable.</summary>
public sealed record WorkflowRunModelCallPartDescriptor
{
    public required WorkflowRunModelCallPart Part { get; init; }
    public required WorkflowRunModelCallPartSource Source { get; init; }
    public long? SizeBytes { get; init; }
    public string? ContentType { get; init; }
    public Guid? ArtifactId { get; init; }
}
