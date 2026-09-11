using CodeSpace.Messages.Agents.Recovery;

namespace CodeSpace.Core.Persistence.Entities;

/// <summary>
/// The persisted form of <see cref="RunCleanupReceipt"/> — one row per (run, resource kind, resource key), upserted
/// rather than appended, so a run's cleanup story is a small readable set. Written on every abandon, most importantly
/// the abandon that happens on a host owning none of the run's resources.
/// </summary>
public sealed class AgentRunCleanupReceiptRecord : IEntity<Guid>
{
    public Guid Id { get; set; }
    public Guid TeamId { get; set; }
    public Guid AgentRunId { get; set; }
    public long FenceEpoch { get; set; }
    public RunResourceKind Kind { get; set; }
    public RunResourceOutcome Outcome { get; set; }
    public string? OwnerHost { get; set; }
    public string? ResourceKey { get; set; }
    public string RecordedByHost { get; set; } = string.Empty;
    public DateTimeOffset RecordedAt { get; set; }
    public string? ErrorCode { get; set; }

    public AgentRun AgentRun { get; set; } = default!;
}
