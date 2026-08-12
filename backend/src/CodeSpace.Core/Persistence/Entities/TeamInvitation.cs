using CodeSpace.Messages.Enums;

namespace CodeSpace.Core.Persistence.Entities;

/// <summary>
/// A standing offer for one address to join one team at one role.
///
/// <para>The link is the credential, so <see cref="TokenHash"/> is all that is kept — the plaintext
/// is returned once, when the invitation is created, and cannot be recovered afterwards. Losing it
/// means regenerating, which is the correct outcome: an invitation you cannot find is one you should
/// not still be able to send.</para>
/// </summary>
public class TeamInvitation : IEntity<Guid>, IAuditable
{
    public Guid Id { get; set; }

    public Guid TeamId { get; set; }

    /// <summary>The address this invitation is bound to. Acceptance must match it, case-insensitively.</summary>
    public string Email { get; set; } = default!;

    public TeamRole Role { get; set; }

    /// <summary>SHA-256 hex of the token. Never the token.</summary>
    public string TokenHash { get; set; } = default!;

    public InvitationStatus Status { get; set; }

    public DateTimeOffset ExpiresAt { get; set; }

    public Guid InvitedByUserId { get; set; }

    public Guid? AcceptedByUserId { get; set; }

    public DateTimeOffset? AcceptedAt { get; set; }

    public DateTimeOffset CreatedDate { get; set; }
    public Guid CreatedBy { get; set; }
    public DateTimeOffset LastModifiedDate { get; set; }
    public Guid LastModifiedBy { get; set; }

    public Team Team { get; set; } = default!;
    public User InvitedBy { get; set; } = default!;
}
