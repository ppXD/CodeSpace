using CodeSpace.Messages.Enums;

namespace CodeSpace.Core.Persistence.Entities;

public class Team : IEntity<Guid>, IAuditable
{
    public Guid Id { get; set; }

    public string Slug { get; set; } = default!;
    public string Name { get; set; } = default!;
    public string? Description { get; set; }
    public Guid OwnerUserId { get; set; }

    /// <summary>
    /// Personal = the user's solo space (one per user, auto-created on signup, never
    /// deleted). Workspace = the standard multi-member team. Default Workspace for any
    /// new row inserted without setting it explicitly — keeps existing seed flows working.
    /// </summary>
    public TeamKind Kind { get; set; } = TeamKind.Workspace;

    public DateTimeOffset CreatedDate { get; set; }
    public Guid CreatedBy { get; set; }
    public DateTimeOffset LastModifiedDate { get; set; }
    public Guid LastModifiedBy { get; set; }
    public DateTimeOffset? DeletedDate { get; set; }

    public User Owner { get; set; } = default!;
    public ICollection<TeamMembership> Memberships { get; set; } = new List<TeamMembership>();
}
