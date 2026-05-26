namespace CodeSpace.Core.Persistence.Entities;

/// <summary>
/// A named namespace for variables. Workflows reference variables in a Project via the
/// dotted path <c>project.{Slug}.{VariableName}</c> — the Project has NO relationship to
/// any workflow / repository / workflow_run; it is purely a variable container.
///
/// <para>Future Project-level resources (cron schedules, billing tags, per-Project RBAC)
/// land on this entity additively without affecting workflows, repos, or runs.</para>
///
/// <para>Tenant boundary: Project belongs to one Team (<see cref="TeamId"/>). Cross-team
/// access is impossible — variable references resolve <c>project.X.Y</c> by querying
/// <c>(team_id, slug=X)</c> within the run's team context.</para>
/// </summary>
public class Project : IEntity<Guid>, IAuditable
{
    public Guid Id { get; set; }

    public Guid TeamId { get; set; }

    /// <summary>
    /// URL-safe + identifier-safe slug. Forms the middle segment of the variable reference
    /// path: <c>project.{Slug}.{VariableName}</c>. Constrained by DB CHECK + app-layer
    /// validation to <c>^[A-Za-z0-9_-]{1,64}$</c> — no dots (would collide with the
    /// resolver's dotted-path syntax), no spaces.
    /// </summary>
    public string Slug { get; set; } = default!;

    /// <summary>Human display name. Free-form; what the operator sees in the UI.</summary>
    public string Name { get; set; } = default!;

    /// <summary>Optional one-line description for operator clarity. Not consumed by the engine.</summary>
    public string? Description { get; set; }

    /// <summary>
    /// Soft-delete timestamp. Soft-deleting a Project keeps the row for audit and lets the
    /// same slug be re-created later. Workflows referencing a soft-deleted Project's slug
    /// fail validation on next save with an actionable "project X not found" error.
    /// </summary>
    public DateTimeOffset? DeletedDate { get; set; }

    public DateTimeOffset CreatedDate { get; set; }
    public Guid CreatedBy { get; set; }
    public DateTimeOffset LastModifiedDate { get; set; }
    public Guid LastModifiedBy { get; set; }

    public Team Team { get; set; } = default!;
}
