namespace CodeSpace.Core.Persistence.Entities;

/// <summary>
/// First-class container for <see cref="Repository"/> and project-scoped variables.
/// Workflows stay at Team level (they're reusable across Projects); a workflow references
/// a project's variables via the dotted path <c>project.{Slug}.{VariableName}</c>.
///
/// <para>Every Team has at least one Project named <c>default</c> (seeded by migration
/// 0022 + auto-created on new-team provisioning). Bind flow attaches new repositories to
/// a caller-chosen Project (defaults to Default).</para>
/// </summary>
public class Project : IEntity<Guid>, IAuditable
{
    public Guid Id { get; set; }

    public Guid TeamId { get; set; }

    /// <summary>
    /// URL-safe + identifier-safe slug — forms the middle segment of variable refs:
    /// <c>project.{Slug}.{VariableName}</c>. Constrained by DB CHECK + app validation
    /// to <c>^[A-Za-z0-9_-]{1,64}$</c> — no dots (would collide with resolver path),
    /// no spaces.
    /// </summary>
    public string Slug { get; set; } = default!;

    /// <summary>Human display name.</summary>
    public string Name { get; set; } = default!;

    /// <summary>Optional one-line description for operator clarity.</summary>
    public string? Description { get; set; }

    /// <summary>Soft-delete; the slug can be reused after delete.</summary>
    public DateTimeOffset? DeletedDate { get; set; }

    public DateTimeOffset CreatedDate { get; set; }
    public Guid CreatedBy { get; set; }
    public DateTimeOffset LastModifiedDate { get; set; }
    public Guid LastModifiedBy { get; set; }

    public Team Team { get; set; } = default!;
}
