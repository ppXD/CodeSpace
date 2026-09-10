namespace CodeSpace.Core.Persistence.Entities;

/// <summary>
/// One team's standing cost cap — at most one row per team, which is why the team id IS the key rather than a
/// surrogate with a uniqueness check bolted on. Its ABSENCE is meaningful: a team with no row falls back to the
/// deployment cap, and a deployment with no cap either keeps the pre-5b-ii behaviour of a per-run ceiling only.
/// </summary>
public class BudgetTeamCap : IAuditable
{
    public Guid TeamId { get; set; }

    public decimal CapUsd { get; set; }

    /// <summary>
    /// The rolling window the committed sum covers — <c>Messages.Budget.TeamCostCap.RollingThirtyDays</c>. Named
    /// <c>CapWindow</c> rather than <c>Window</c> because the snake_case convention would map the latter to a
    /// <c>window</c> column, which is a reserved word in PostgreSQL.
    /// </summary>
    public string CapWindow { get; set; } = string.Empty;

    public DateTimeOffset CreatedDate { get; set; }
    public Guid CreatedBy { get; set; }
    public DateTimeOffset LastModifiedDate { get; set; }
    public Guid LastModifiedBy { get; set; }
}
