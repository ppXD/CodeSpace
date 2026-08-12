using CodeSpace.Messages.Authorization;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Dtos.Invitations;
using CodeSpace.Messages.Enums;
using CodeSpace.Messages.Mediation;
using MediatR;

namespace CodeSpace.Messages.Commands.Invitations;

/// <summary>
/// Create a standing offer for one address to join the current team. Answers with the link ONCE —
/// it is not stored and cannot be read again.
/// </summary>
public sealed record CreateTeamInvitationCommand : ICommand<CreateInvitationResult>, IRequireTeamPermission
{
    public string RequiredPermission => TeamPermissions.MembersManage;

    public required string Email { get; init; }

    /// <summary>The role the invitee lands on. Clamped server-side to the granter's own rank.</summary>
    public required TeamRole Role { get; init; }
}

public sealed record RevokeTeamInvitationCommand : ICommand<Unit>, IRequireTeamPermission
{
    public string RequiredPermission => TeamPermissions.MembersManage;

    /// <summary>Bound from the route (Rule 17), never the body.</summary>
    public Guid InvitationId { get; init; }
}

/// <summary>
/// Mint a replacement token for a pending invitation. The previous link stops working immediately —
/// which is the reason to reach for this rather than revoke-and-reinvite.
/// </summary>
public sealed record RegenerateTeamInvitationCommand : ICommand<CreateInvitationResult>, IRequireTeamPermission
{
    public string RequiredPermission => TeamPermissions.MembersManage;

    public Guid InvitationId { get; init; }
}

/// <summary>
/// Spend an invitation. ANONYMOUS by design — the invitee has no session, and requiring one would
/// send them to sign in for an account they do not have. The token in the route is the credential.
///
/// <para>No authorization marker for that reason; <c>RequestAuthorizationInventoryTests</c> names it
/// so the absence stays a decision someone made rather than one someone forgot.</para>
/// </summary>
public sealed record AcceptInvitationCommand : ICommand<Auth.SignInResponse>, IBypassPasswordRotationGuard
{
    /// <summary>
    /// Merged in from the route by the controller; the body never carries it.
    ///
    /// <para>Nullable NOT because it is optional but because <c>[ApiController]</c> treats a
    /// non-nullable string property as implicitly required, and rejects the body for missing the very
    /// field the route supplies — a 400 raised by model binding before the controller can merge the
    /// real value in, and before any filter can shape it into something a client can read.</para>
    /// </summary>
    public string? Token { get; init; }

    /// <summary>Omitted when the address already has an account — the name comes from it.</summary>
    public string? Name { get; init; }

    public string? Password { get; init; }
}
