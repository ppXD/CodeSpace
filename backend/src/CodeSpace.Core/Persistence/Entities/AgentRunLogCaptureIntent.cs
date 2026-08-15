namespace CodeSpace.Core.Persistence.Entities;

/// <summary>
/// Durable expectation that one exact worker/capture session should produce one versioned Agent Run log stream.
/// The row is declared before stream open, so a missing stream is distinguishable from a captured zero-byte stream.
/// Recovery ownership is an independent expiring fence and never changes the owning AgentRun's lifecycle result.
/// </summary>
public sealed class AgentRunLogCaptureIntent : IEntity<Guid>
{
    public Guid Id { get; set; }
    public Guid TeamId { get; set; }
    public Guid AgentRunId { get; set; }
    public long WorkerFenceEpoch { get; set; }
    public Guid CaptureSessionId { get; set; }
    public string StreamKind { get; set; } = string.Empty;
    public string ContentType { get; set; } = "application/octet-stream";
    public string? ContentEncoding { get; set; }
    public string CaptureSource { get; set; } = string.Empty;
    public Guid? StreamId { get; set; }
    public AgentRunLogCaptureIntentState State { get; set; } = AgentRunLogCaptureIntentState.Expected;
    public long Revision { get; set; } = 1;
    public int RecoveryAttemptCount { get; set; }
    public DateTimeOffset? RecoveryStartedAt { get; set; }
    public DateTimeOffset NextRecoveryAt { get; set; }
    public Guid? RecoveryOwnerId { get; set; }
    public long RecoveryFenceEpoch { get; set; }
    public DateTimeOffset? RecoveryLeaseExpiresAt { get; set; }
    public string? LastErrorCode { get; set; }
    public string? LastErrorMessage { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset LastModifiedAt { get; set; }
    public DateTimeOffset? TerminalObservedAt { get; set; }
    public DateTimeOffset? TerminalAt { get; set; }
    public uint Xmin { get; set; }

    public AgentRun AgentRun { get; set; } = default!;
    public AgentRunLogStream? Stream { get; set; }
}

/// <summary>Monotonic capture-health state only; it is never an AgentRun completion signal.</summary>
public enum AgentRunLogCaptureIntentState
{
    Expected,
    Opened,
    SourceFinalized,
    Completed,
    CaptureFailed,
    Superseded,
    ExternalStateIndeterminate,
}
