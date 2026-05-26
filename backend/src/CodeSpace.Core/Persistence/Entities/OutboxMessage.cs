using CodeSpace.Messages.Enums;

namespace CodeSpace.Core.Persistence.Entities;

public class OutboxMessage : IEntity<Guid>, IAuditable
{
    public Guid Id { get; set; }

    /// <summary>Domain object this message relates to (e.g. "Repository"). Diagnostic-only — not used for dispatch.</summary>
    public string AggregateType { get; set; } = default!;

    /// <summary>FK-like id of the aggregate. Diagnostic — used for "list messages stuck on repo X" queries.</summary>
    public Guid AggregateId { get; set; }

    /// <summary>Discriminator the dispatcher uses to pick the right IOutboxMessageHandler. Stable string contract; never rename without a migration.</summary>
    public string MessageType { get; set; } = default!;

    /// <summary>Handler-specific JSON. Each handler owns the schema of its own payload.</summary>
    public string Payload { get; set; } = default!;

    public OutboxStatus Status { get; set; } = OutboxStatus.Pending;
    public int Attempts { get; set; }
    public DateTimeOffset? LastAttemptedDate { get; set; }
    public string? LastError { get; set; }
    public DateTimeOffset NextAttemptDate { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// Worker identity holding the lease while <c>Status == Claimed</c>. NULL otherwise.
    /// Diagnostic only; concurrency safety comes from <c>SKIP LOCKED</c> on the atomic claim UPDATE.
    /// </summary>
    public Guid? ClaimedBy { get; set; }

    /// <summary>When the current lease was issued. Null when <c>Status != Claimed</c>.</summary>
    public DateTimeOffset? ClaimedAt { get; set; }

    /// <summary>
    /// When the current lease expires. <c>OutboxLeaseReaper</c> resets rows with
    /// <c>Status == Claimed AND LeaseUntil &lt; now()</c> back to <c>Pending</c>, so a
    /// crashed worker's in-flight row gets retried instead of frozen.
    /// </summary>
    public DateTimeOffset? LeaseUntil { get; set; }

    public DateTimeOffset CreatedDate { get; set; }
    public Guid CreatedBy { get; set; }
    public DateTimeOffset LastModifiedDate { get; set; }
    public Guid LastModifiedBy { get; set; }
}
