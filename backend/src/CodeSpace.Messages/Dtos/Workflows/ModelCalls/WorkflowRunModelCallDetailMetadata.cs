using CodeSpace.Messages.Contracts;

namespace CodeSpace.Messages.Dtos.Workflows.ModelCalls;

/// <summary>
/// Byte-free view of one stable logical Workflow Run model-call id. Request/response/error bodies are represented
/// only by typed references and are fetched through the bounded body endpoint when explicitly opened.
/// </summary>
public sealed record WorkflowRunModelCallDetailMetadata
{
    public required Guid WorkflowRunModelCallId { get; init; }
    public required Guid RunId { get; init; }
    public required long CallOrdinal { get; init; }
    public string? NodeId { get; init; }
    public required string IterationKey { get; init; }
    public Guid? WorkPlanId { get; init; }
    public int? PlanVersion { get; init; }
    public string? WorkUnitId { get; init; }
    public string? WorkUnitContractHash { get; init; }
    public Guid? ExecutionAttemptId { get; init; }
    public int? ExecutionAttemptOrdinal { get; init; }
    public int? ExecutionGeneration { get; init; }
    public required string Purpose { get; init; }
    public string? RequestedProvider { get; init; }
    public string? RequestedModel { get; init; }
    public Guid? RequestedModelRowId { get; init; }
    public string? SelectionPolicy { get; init; }
    public string? SourceKind { get; init; }
    public Guid? SourceCorrelationId { get; init; }
    public required string CaptureSource { get; init; }
    public required WorkflowRunCaptureCompleteness CaptureCompleteness { get; init; }
    public required int SchemaVersion { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public required IReadOnlyList<WorkflowRunModelCallBodyDescriptor> Bodies { get; init; }
    public required IReadOnlyList<WorkflowRunModelCallAttemptMetadata> Attempts { get; init; }
}

public sealed record WorkflowRunModelCallAttemptMetadata
{
    public required Guid AttemptId { get; init; }
    public required int AttemptOrdinal { get; init; }
    public string? EffectiveProvider { get; init; }
    public string? EffectiveModel { get; init; }
    public Guid? EffectiveModelRowId { get; init; }
    public string? TransportKind { get; init; }
    public string? EndpointFingerprint { get; init; }
    public string? ProviderRequestId { get; init; }
    public required string Status { get; init; }
    public string? ErrorCode { get; init; }
    public string? FinishReason { get; init; }
    public int? HttpStatusCode { get; init; }
    public required string CaptureSource { get; init; }
    public required WorkflowRunCaptureCompleteness CaptureCompleteness { get; init; }
    public required WorkflowRunModelCallSourceEvidence SourceEvidence { get; init; }
    public Guid? SourceStartedRecordId { get; init; }
    public Guid? SourceTerminalRecordId { get; init; }
    public required int SourceEvidenceRevision { get; init; }
    public required WorkflowRunModelCallUsageMetadata Usage { get; init; }
    public decimal? CostAmount { get; init; }
    public string? CostCurrency { get; init; }
    public string? PricingVersion { get; init; }
    public required DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset? FirstTokenAt { get; init; }
    public DateTimeOffset? CompletedAt { get; init; }
    public required int SchemaVersion { get; init; }
    public required IReadOnlyList<WorkflowRunModelCallBodyDescriptor> Bodies { get; init; }
}

public sealed record WorkflowRunModelCallUsageMetadata
{
    public long? InputTokens { get; init; }
    public long? OutputTokens { get; init; }
    public long? CacheReadTokens { get; init; }
    public long? CacheWriteTokens { get; init; }
    public long? ReasoningTokens { get; init; }
}

public sealed record WorkflowRunModelCallBodyDescriptor
{
    public required WorkflowRunModelCallBody Body { get; init; }
    public Guid? AttemptId { get; init; }
    public Guid? ArtifactId { get; init; }
    public required WorkflowRunModelCallBodyReferenceState ReferenceState { get; init; }
    public required WorkflowRunCaptureCompleteness CaptureCompleteness { get; init; }
}

public enum WorkflowRunModelCallBody
{
    LogicalRequest = 0,
    AttemptRequest = 1,
    AttemptResponse = 2,
    AttemptError = 3,
}

/// <summary>
/// Metadata state only. Referenced means an artifact id was captured, not that its bytes currently exist or pass
/// integrity checks; those facts are returned by the bounded body read.
/// </summary>
public enum WorkflowRunModelCallBodyReferenceState
{
    Referenced = 0,
    NotRecorded = 1,
    Redacted = 2,
    Partial = 3,
    Unavailable = 4,
    Corrupt = 5,
    LegacyUnknown = 6,
}

public enum WorkflowRunModelCallSourceEvidence
{
    Native = 0,
    TerminalOnly = 1,
    StartedAndTerminal = 2,
    LateStartAttached = 3,
}

/// <summary>One bounded UTF-8 page from a stable model-call body reference.</summary>
public sealed record WorkflowRunModelCallBodyPage
{
    public required WorkflowRunModelCallBody Body { get; init; }
    public Guid? AttemptId { get; init; }
    public required WorkflowRunCaptureCompleteness CaptureCompleteness { get; init; }
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
