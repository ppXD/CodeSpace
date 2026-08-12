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

    /// <summary>True when that address already has an account, so the page asks them to sign in rather than to set a password.</summary>
    public required bool AccountExists { get; init; }
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
    public required DateTimeOffset ExpiresAt { get; init; }
    public required bool IsExpired { get; init; }
}
