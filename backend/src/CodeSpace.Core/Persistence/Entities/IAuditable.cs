namespace CodeSpace.Core.Persistence.Entities;

public interface IAuditable
{
    DateTimeOffset CreatedDate { get; set; }
    Guid CreatedBy { get; set; }
    DateTimeOffset LastModifiedDate { get; set; }
    Guid LastModifiedBy { get; set; }
}
