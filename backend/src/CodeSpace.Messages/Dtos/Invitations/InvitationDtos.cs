using CodeSpace.Messages.Enums;

namespace CodeSpace.Messages.Dtos.Invitations;

/// <summary>
/// What an unauthenticated visitor is told about a token that checks out.
///
/// <para>Nothing here is returned for a token that does not, which is the point: an invalid guess
/// must not confirm that a team exists, let alone name it.</para>
/// </summary>
public sealed record InvitationPreview
{
    public required string TeamName { get; init; }

    /// <summary>Display name of the member who sent it, for "X invited you to Y".</summary>
    public required string InvitedByName { get; init; }

    public required TeamRole Role { get; init; }

    /// <summary>The address the invitation is bound to. Shown so the invitee can tell they have the right link.</summary>
    public required string Email { get; init; }

    public required DateTimeOffset ExpiresAt { get; init; }

    /// <summary>
    /// True only when the request carries the session of the account this invitation is addressed to, so the page
    /// asks that account to join rather than to set a password.
    ///
    /// <para>A fact about the caller, never about the address. It replaced a flag saying whether the address had an
    /// account at all, which made this anonymous endpoint an existence oracle: any account that can open a workspace
    /// could invite an arbitrary address and read the answer.</para>
    /// </summary>
    public required bool ForSignedInAccount { get; init; }
}

/// <summary>
/// The one and only time the token is readable. <see cref="InviteUrl"/> is not stored and cannot be
/// recovered — a member who loses it regenerates, which invalidates the old link by design.
/// </summary>
public sealed record CreateInvitationResult
{
    public required Guid InvitationId { get; init; }
    public required string InviteUrl { get; init; }
    public required DateTimeOffset ExpiresAt { get; init; }
}

/// <summary>A pending invitation as the members screen lists it. Carries no token, by construction.</summary>
public sealed record TeamInvitationSummary
{
    public required Guid Id { get; init; }
    public required string Email { get; init; }
    public required TeamRole Role { get; init; }
    public required string InvitedByName { get; init; }

    /// <summary>
    /// Who sent it — the half of the reach rule a role cannot answer. An invitation at the caller's own role is theirs to
    /// regenerate or revoke only if they sent it, so the members screen needs the sender to hide what would be refused.
    /// </summary>
    public required Guid InvitedByUserId { get; init; }

    public required DateTimeOffset ExpiresAt { get; init; }
    public required bool IsExpired { get; init; }
}
