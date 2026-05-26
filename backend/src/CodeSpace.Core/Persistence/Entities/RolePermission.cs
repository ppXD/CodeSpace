namespace CodeSpace.Core.Persistence.Entities;

/// <summary>Many-to-many: role grants this permission.</summary>
public class RolePermission : IEntity<Guid>, IAuditable
{
    public Guid Id { get; set; }

    public Guid RoleId { get; set; }
    public Guid PermissionId { get; set; }

    public DateTimeOffset CreatedDate { get; set; }
    public Guid CreatedBy { get; set; }
    public DateTimeOffset LastModifiedDate { get; set; }
    public Guid LastModifiedBy { get; set; }
}
