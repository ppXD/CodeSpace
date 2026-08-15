namespace CodeSpace.Core.Persistence.Entities;

/// <summary>
/// One immutable contiguous byte range in an <see cref="AgentRunLogStream"/>. The database admits a segment only when
/// its worker fence is current, its ordinal/offset match the locked stream head, and its CAS object has a verified
/// available location with the exact length. PostgreSQL therefore stores metadata only, never unbounded CLI output.
/// </summary>
public sealed class AgentRunLogSegment : IEntity<Guid>
{
    public Guid Id { get; set; }
    public Guid TeamId { get; set; }
    public Guid AgentRunId { get; set; }
    public Guid StreamId { get; set; }
    public long SegmentOrdinal { get; set; }
    public long StartOffsetBytes { get; set; }
    public long LengthBytes { get; set; }
    public long SourceStartOffsetBytes { get; set; }
    public long SourceLengthBytes { get; set; }
    public Guid ArtifactObjectId { get; set; }
    public long WorkerFenceEpoch { get; set; }
    public Guid CaptureSessionId { get; set; }
    public DateTimeOffset FirstObservedAt { get; set; }
    public DateTimeOffset LastObservedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public int SchemaVersion { get; set; } = 1;

    public AgentRunLogStream Stream { get; set; } = default!;
    public AgentRunLogCaptureSession CaptureSession { get; set; } = default!;
    public ArtifactObject ArtifactObject { get; set; } = default!;
}
