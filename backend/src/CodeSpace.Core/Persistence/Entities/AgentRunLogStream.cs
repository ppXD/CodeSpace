namespace CodeSpace.Core.Persistence.Entities;

/// <summary>
/// Mutable monotonic head for one byte-addressed log stream emitted by an <see cref="AgentRun"/>. The open,
/// major-versioned <see cref="StreamKind"/> makes the archive harness-neutral; actual bytes live in immutable
/// <see cref="ArtifactObject"/> rows referenced by append-only <see cref="AgentRunLogSegment"/> records.
/// </summary>
public sealed class AgentRunLogStream : IEntity<Guid>
{
    public Guid Id { get; set; }
    public Guid TeamId { get; set; }
    public Guid AgentRunId { get; set; }
    public long? WorkerFenceEpoch { get; set; }
    public Guid? CaptureSessionId { get; set; }
    public string StreamKind { get; set; } = string.Empty;
    public string ContentType { get; set; } = "application/octet-stream";
    public string? ContentEncoding { get; set; }
    public string CaptureSource { get; set; } = string.Empty;
    public ArtifactRetention Retention { get; set; } = ArtifactRetention.Run;
    public DateTimeOffset? ExpiresAt { get; set; }
    public AgentRunLogStreamState State { get; set; } = AgentRunLogStreamState.Open;
    public long Revision { get; set; } = 1;
    public long SegmentCount { get; set; }
    public long TotalBytes { get; set; }
    public long NextSegmentOrdinal { get; set; } = 1;
    public long NextOffsetBytes { get; set; }
    public ArtifactDigestAlgorithm? ContentDigestAlgorithm { get; set; }
    public byte[]? ContentDigest { get; set; }
    public int SchemaVersion { get; set; } = 2;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset LastModifiedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public string? ErrorCode { get; set; }
    public string? ErrorMessage { get; set; }
    public uint Xmin { get; set; }

    public AgentRun AgentRun { get; set; } = default!;
    public ICollection<AgentRunLogSegment> Segments { get; set; } = new List<AgentRunLogSegment>();
}

/// <summary>Capture state, not the Agent Run's task outcome. Every non-Open state is terminal.</summary>
public enum AgentRunLogStreamState
{
    Open,
    Completed,
    Truncated,
    Unavailable,
    Corrupt,
    CaptureFailed,
}
