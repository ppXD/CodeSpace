namespace CodeSpace.Core.Persistence.Entities;

public class Permission : IEntity<Guid>, IAuditable
{
    public Guid Id { get; set; }

    /// <summary>Machine name (e.g. "repositories:write"). Referenced via Permissions.Xxx constants.</summary>
    public string Name { get; set; } = default!;

    public string? DisplayName { get; set; }
    public string? Description { get; set; }

    public bool IsSystem { get; set; }

    public DateTimeOffset CreatedDate { get; set; }
    public Guid CreatedBy { get; set; }
    public DateTimeOffset LastModifiedDate { get; set; }
    public Guid LastModifiedBy { get; set; }
}
