namespace CodeSpace.Core.Persistence.Entities;

/// <summary>
/// One logical budget claim with a frozen admission intent and a separately recorded actual cost. An uncertain
/// claim retains ReservedUsd; this is an estimate unless its producer proves a provider wire upper bound.
/// ScopeKey identifies retries of the same claim, not permission for an additional physical request.
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
    /// <summary>The cap of the original reservation intent. Null on legacy rows whose complete intent was not persisted.</summary>
    public decimal? CapUsd { get; set; }
    public decimal? SettledUsd { get; set; }
    public string PriceVersion { get; set; } = string.Empty;
    public DateTimeOffset? ExpiresAt { get; set; }
    public DateTimeOffset CreatedDate { get; set; }
    public Guid CreatedBy { get; set; }
    public DateTimeOffset LastModifiedDate { get; set; }
    public Guid LastModifiedBy { get; set; }
}
