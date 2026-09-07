namespace CodeSpace.Core.Persistence.Entities;

/// <summary>Durable historical full-part-read progress for one exact finalized v3 log head. This is not a retention lease.</summary>
public sealed class AgentRunLogVerification : IEntity<Guid>
{
    public Guid Id { get; set; }
    public Guid TeamId { get; set; }
    public Guid AgentRunId { get; set; }
    public Guid StreamId { get; set; }
    public long WorkerFenceEpoch { get; set; }
    public Guid CaptureSessionId { get; set; }
    public long StreamRevision { get; set; }
    public long SegmentCount { get; set; }
    public long TotalBytes { get; set; }
    public long SourceOffsetBytes { get; set; }
    public long NextSegmentOrdinal { get; set; } = 1;
    public long VerifiedBytes { get; set; }
    public byte[] Accumulator { get; set; } = [];
    public long Revision { get; set; } = 1;
    public Guid? RecoveryIntentId { get; set; }
    public Guid? RecoveryOwnerId { get; set; }
    public long? RecoveryFenceEpoch { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset LastModifiedAt { get; set; }
    public DateTimeOffset? SealedAt { get; set; }
    public byte[]? ManifestDigest { get; set; }
    public uint Xmin { get; set; }
}
