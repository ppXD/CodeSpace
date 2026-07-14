namespace CodeSpace.Core.Persistence.Entities;

/// <summary>
/// W-hard: one atomic budget reservation — see 0104's header for the state machine and THE admission invariant
/// (settled + live ≤ hard cap, enforced under a per-run advisory lock). <see cref="ScopeKey"/> is the idempotency
/// coordinate (an attempt id, a turn key) — a crash-replayed producer lands on its own row.
/// </summary>
public class BudgetReservation : IEntity<Guid>, IAuditable
{
    public Guid Id { get; set; }
    public Guid TeamId { get; set; }
    public Guid WorkflowRunId { get; set; }
    public Guid? ParentReservationId { get; set; }
    public string Kind { get; set; } = string.Empty;
    public string ScopeKey { get; set; } = string.Empty;
    public string State { get; set; } = string.Empty;
    public decimal ReservedUsd { get; set; }
    public decimal? SettledUsd { get; set; }
    public string PriceVersion { get; set; } = string.Empty;
    public DateTimeOffset? ExpiresAt { get; set; }
    public DateTimeOffset CreatedDate { get; set; }
    public Guid CreatedBy { get; set; }
    public DateTimeOffset LastModifiedDate { get; set; }
    public Guid LastModifiedBy { get; set; }
}
