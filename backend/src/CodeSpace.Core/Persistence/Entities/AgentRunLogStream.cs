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
    /// <summary>Monotonic raw-source progress, kept separate because redaction can change stored byte length.</summary>
    public long SourceOffsetBytes { get; set; }
    /// <summary>Raw-source head at which the currently claimed spool started; local reads resume at source minus base.</summary>
    public long CaptureSourceBaseOffsetBytes { get; set; }
    /// <summary>Durable proof that the currently claimed spool source reached a final drain at its committed source head.</summary>
    public DateTimeOffset? CaptureFinalizedAt { get; set; }
    /// <summary>
    /// When the producer's remote storage first refused a segment it is STILL holding. It is the difference between a
    /// stream that is quietly finalizing and one whose bytes are queued behind an outage: the head is frozen on
    /// purpose, nothing was dropped, and a reader can say so instead of reporting progress that is not happening.
    /// Present exactly with <see cref="RemoteStallCode"/>. It is cleared when a segment commits again inside the same
    /// capture session, and otherwise by the NEXT capture claim — a marker is one producer's statement about bytes it
    /// is holding in memory, so it cannot outlive the session that made it. A row that PARKED keeps its marker on
    /// purpose: it is the durable record of why capture ended, and a reader only treats the marker as live health
    /// while the stream is still Open.
    /// </summary>
    public DateTimeOffset? RemoteStallSince { get; set; }
    /// <summary>The typed refusal being waited out, in the capture bridge's error-code vocabulary. Never a second reason vocabulary, and never parsed.</summary>
    public string? RemoteStallCode { get; set; }
    /// <summary>
    /// The earliest instant this stream's bytes may be reclaimed, written by the retention reaper on the FIRST sweep
    /// that found the stream terminal, past its rule and cited by nobody. Null means no sweep has ever proposed it —
    /// which is what every stream a live worker is still capturing reads, and what the whole table read before the
    /// plane existed.
    /// </summary>
    public DateTimeOffset? RetainUntil { get; set; }

    /// <summary>
    /// When this stream's segment bytes were reclaimed. The head row deliberately outlives its bytes: a reader that
    /// finds nothing cannot tell "purged by policy" from "lost", so the tombstone is what lets the Room say purged.
    /// </summary>
    public DateTimeOffset? PurgedAt { get; set; }

    public ArtifactDigestAlgorithm? ContentDigestAlgorithm { get; set; }
    public byte[]? ContentDigest { get; set; }
    public byte[]? ManifestDigest { get; set; }
    public int SchemaVersion { get; set; } = 3;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset LastModifiedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public string? ErrorCode { get; set; }
    public string? ErrorMessage { get; set; }
    public uint Xmin { get; set; }

    public AgentRun AgentRun { get; set; } = default!;
    public ICollection<AgentRunLogSegment> Segments { get; set; } = new List<AgentRunLogSegment>();
    public ICollection<AgentRunLogCaptureSession> CaptureSessions { get; set; } = new List<AgentRunLogCaptureSession>();
    public ICollection<AgentRunLogCaptureIntent> CaptureIntents { get; set; } = new List<AgentRunLogCaptureIntent>();
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
