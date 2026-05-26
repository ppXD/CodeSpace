namespace CodeSpace.Core.Persistence.Entities;

/// <summary>
/// Direct user ↔ permission grant — bypasses role membership. Used for fine-grained
/// per-user overrides (e.g. grant "billing:read" to a specific user without assigning
/// them a whole Role). v1 schema is provisioned; admin UI lands later.
/// </summary>
public class UserPermission : IEntity<Guid>, IAuditable
{
    public Guid Id { get; set; }

    public Guid UserId { get; set; }
    public Guid PermissionId { get; set; }

    public DateTimeOffset CreatedDate { get; set; }
    public Guid CreatedBy { get; set; }
    public DateTimeOffset LastModifiedDate { get; set; }
    public Guid LastModifiedBy { get; set; }
}
