namespace CodeSpace.Core.Persistence.Entities;

/// <summary>Many-to-many assignment: user holds this role.</summary>
public class RoleUser : IEntity<Guid>, IAuditable
{
    public Guid Id { get; set; }

    public Guid RoleId { get; set; }
    public Guid UserId { get; set; }

    public DateTimeOffset CreatedDate { get; set; }
    public Guid CreatedBy { get; set; }
    public DateTimeOffset LastModifiedDate { get; set; }
    public Guid LastModifiedBy { get; set; }
}
