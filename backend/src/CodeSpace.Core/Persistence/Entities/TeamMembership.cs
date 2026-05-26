using CodeSpace.Messages.Enums;

namespace CodeSpace.Core.Persistence.Entities;

public class TeamMembership : IEntity<Guid>, IAuditable
{
    public Guid Id { get; set; }

    public Guid TeamId { get; set; }
    public Guid UserId { get; set; }
    public TeamRole Role { get; set; }

    public DateTimeOffset CreatedDate { get; set; }
    public Guid CreatedBy { get; set; }
    public DateTimeOffset LastModifiedDate { get; set; }
    public Guid LastModifiedBy { get; set; }

    public Team Team { get; set; } = default!;
    public User User { get; set; } = default!;
}
