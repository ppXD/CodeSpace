namespace CodeSpace.Core.Persistence.Entities;

/// <summary>An append-only, bounded execution checkpoint attached to one paid paired cell. Kinds are open and runner-owned; the core persists identity without interpreting task or provider semantics.</summary>
public sealed class PairedQualificationCellCheckpoint : IEntity<Guid>, IAuditable
{
    public Guid Id { get; set; }
    public Guid AdmissionId { get; set; }
    public string Kind { get; set; } = string.Empty;
    public string PayloadJson { get; set; } = string.Empty;
    public DateTimeOffset CreatedDate { get; set; }
    public Guid CreatedBy { get; set; }
    public DateTimeOffset LastModifiedDate { get; set; }
    public Guid LastModifiedBy { get; set; }
}
