using CodeSpace.Messages.Enums;

namespace CodeSpace.Core.Persistence.Entities;

public class Team : IEntity<Guid>, IAuditable
{
    public Guid Id { get; set; }

    public string Slug { get; set; } = default!;
    public string Name { get; set; } = default!;
    public string? Description { get; set; }

    /// <summary>
    /// The account a Personal team IS, and NULL on every other kind of team. NOT the owner —
    /// ownership is the Owner role on a <see cref="TeamMembership"/> row and nothing else.
    ///
    /// <para>This is the denormalised owner column that used to answer "who owns this team" in
    /// parallel with the membership row, and drifted from it every time someone left, was removed or
    /// was demoted. All that is left of it is the one thing the membership table cannot express:
    /// <c>idx_team_personal_per_user_active</c> enforces one active Personal team per user as a
    /// partial unique index, and Postgres cannot build a unique index on <c>team</c> out of a column
    /// on <c>team_membership</c>. Read it for nothing else — the index is the only consumer.</para>
    /// </summary>
    public Guid? PersonalForUserId { get; set; }

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

    public User? PersonalFor { get; set; }
    public ICollection<TeamMembership> Memberships { get; set; } = new List<TeamMembership>();
}
