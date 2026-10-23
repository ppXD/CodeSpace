namespace CodeSpace.Core.Persistence.Entities;

public class WorkflowExternalTrigger : IEntity<Guid>, IAuditable
{
    public Guid Id { get; set; }
    public Guid TeamId { get; set; }
    public Guid WorkflowId { get; set; }
    public string NodeId { get; set; } = default!;
    public string Slug { get; set; } = default!;
    public string KeyDigest { get; set; } = default!;
    public string KeyCiphertext { get; set; } = default!;
    public DateTimeOffset CreatedDate { get; set; }
    public Guid CreatedBy { get; set; }
    public DateTimeOffset LastModifiedDate { get; set; }
    public Guid LastModifiedBy { get; set; }
    public DateTimeOffset? DeletedDate { get; set; }
}
