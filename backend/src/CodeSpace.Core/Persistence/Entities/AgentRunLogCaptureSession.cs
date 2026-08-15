namespace CodeSpace.Core.Persistence.Entities;

/// <summary>
/// Append-preserved identity and monotonic health receipt for one native durable source instance (one spool) feeding
/// an Agent Run log stream. Empty spools are retained too; stream heads point at the current session while prior
/// finalized/failed sessions remain replayable.
/// </summary>
public sealed class AgentRunLogCaptureSession : IEntity<Guid>
{
    public Guid Id { get; set; }
    public Guid TeamId { get; set; }
    public Guid AgentRunId { get; set; }
    public Guid StreamId { get; set; }
    public Guid CaptureSessionId { get; set; }
    public long InitialWorkerFenceEpoch { get; set; }
    public long CurrentWorkerFenceEpoch { get; set; }
    public long SourceBaseOffsetBytes { get; set; }
    public long SourceOffsetBytes { get; set; }
    public AgentRunLogCaptureSessionState State { get; set; } = AgentRunLogCaptureSessionState.Open;
    public long Revision { get; set; } = 1;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset LastObservedAt { get; set; }
    public DateTimeOffset? FinalizedAt { get; set; }
    public string? ErrorCode { get; set; }
    public string? ErrorMessage { get; set; }
    public uint Xmin { get; set; }

    public AgentRunLogStream Stream { get; set; } = default!;
    public ICollection<AgentRunLogSegment> Segments { get; set; } = new List<AgentRunLogSegment>();
}

public enum AgentRunLogCaptureSessionState
{
    Open,
    Finalized,
    CaptureFailed,
}
