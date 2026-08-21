namespace CodeSpace.Core.Persistence.Entities;

/// <summary>
/// Durable, retryable declaration that one exact immutable interaction-tape field must become a model-call body.
/// Projection only appends this metadata; a separate leased materializer may perform artifact I/O after commit.
/// The source identity is permanent, so a transient store failure can never consume the only copy of the body.
/// </summary>
public sealed class WorkflowRunModelCallBodyCapture : IEntity<Guid>
{
    public Guid Id { get; set; }
    public Guid TeamId { get; set; }
    public Guid WorkflowRunId { get; set; }
    public Guid ModelCallId { get; set; }
    public Guid ModelCallAttemptId { get; set; }
    public WorkflowRunModelCallBodyKind BodyKind { get; set; }
    public string SourceKind { get; set; } = string.Empty;
    public Guid SourceRecordId { get; set; }
    public string SourceProperty { get; set; } = string.Empty;
    public WorkflowRunModelCallBodyCaptureState State { get; set; } = WorkflowRunModelCallBodyCaptureState.Pending;
    public Guid? ArtifactId { get; set; }
    public string? SourceSha256 { get; set; }
    public long? SizeBytes { get; set; }
    public string? ContentType { get; set; }
    public string? MaterializationFormat { get; set; }
    public int MaterializationAttemptCount { get; set; }
    public DateTimeOffset NextMaterializationAt { get; set; }
    public Guid? LeaseOwnerId { get; set; }
    public long LeaseFence { get; set; }
    public DateTimeOffset? LeaseExpiresAt { get; set; }
    public string? LastErrorCode { get; set; }
    public string? LastErrorMessage { get; set; }
    public long Revision { get; set; } = 1;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset LastModifiedAt { get; set; }
    public DateTimeOffset? TerminalAt { get; set; }
    public uint Xmin { get; set; }

    public WorkflowRunModelCall ModelCall { get; set; } = default!;
    public WorkflowRunModelCallAttempt ModelCallAttempt { get; set; } = default!;
    public WorkflowRunRecord SourceRecord { get; set; } = default!;
}

public enum WorkflowRunModelCallBodyKind
{
    LogicalRequest,
    AttemptResponse,
    AttemptError,
}

/// <summary>Capture health only. It never participates in execution, completion, or terminal authority.</summary>
public enum WorkflowRunModelCallBodyCaptureState
{
    Pending,
    Available,
    NotRecorded,
    Corrupt,
    CaptureFailed,
    ExternalStateIndeterminate,
}

public static class WorkflowRunModelCallBodyMaterializationFormats
{
    public const string ExternalArtifact = "external-artifact/v1";
    public const string Utf8StringEnvelope = "utf8-string-envelope/v1";
    public const string JsonEnvelope = "json-envelope/v1";
    public const string EnvelopeContentType = "application/vnd.codespace.workflow-model-call-body";
    public const int EnvelopeHeaderLength = 8;

    public static ReadOnlySpan<byte> Header(string format) => format switch
    {
        Utf8StringEnvelope => "CSMCB1S\n"u8,
        JsonEnvelope => "CSMCB1J\n"u8,
        _ => ReadOnlySpan<byte>.Empty,
    };
}
