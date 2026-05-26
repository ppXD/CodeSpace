namespace CodeSpace.Core.Persistence.Entities;

public class Role : IEntity<Guid>, IAuditable
{
    public Guid Id { get; set; }

    /// <summary>Machine name — referenced by code via Roles.Xxx constants.</summary>
    public string Name { get; set; } = default!;

    /// <summary>Human-readable name shown in admin UI.</summary>
    public string? DisplayName { get; set; }

    public string? Description { get; set; }

    /// <summary>System roles (e.g. Admin) cannot be deleted via UI — only via SQL.</summary>
    public bool IsSystem { get; set; }

    /// <summary>Disabled role still exists but contributes no permissions.</summary>
    public bool Status { get; set; } = true;

    public DateTimeOffset CreatedDate { get; set; }
    public Guid CreatedBy { get; set; }
    public DateTimeOffset LastModifiedDate { get; set; }
    public Guid LastModifiedBy { get; set; }
}
