using CodeSpace.Messages.Enums;
using CodeSpace.Messages.Failures;

namespace CodeSpace.Messages.Exceptions;

/// <summary>
/// The token does not name a usable invitation — expired, revoked, already spent, or never real.
///
/// <para>One type for all four on purpose. Telling them apart would let anyone holding a random
/// token learn which guesses were once valid, and the invitee can do the same thing about it either
/// way: ask for a new link.</para>
/// </summary>
public sealed class InvitationNotUsableException : Exception, IFailure
{
    public InvitationNotUsableException() : base("The invitation token is not usable.") { }

    public FailureKind Kind => FailureKind.NotFound;

    public string Code => FailureCodes.InvitationNotUsable;

    public string? ClientMessage => "This invitation link is no longer valid.";
}

/// <summary>
/// Someone signed in tried to accept an invitation addressed to a different account.
/// </summary>
public sealed class InvitationEmailMismatchException : Exception, IFailure
{
    public InvitationEmailMismatchException() : base("The signed-in account does not match the invited address.") { }

    public FailureKind Kind => FailureKind.Forbidden;

    public string Code => FailureCodes.InvitationEmailMismatch;

    public string? ClientMessage => "This invitation was sent to a different account. Sign in as that account to accept it.";
}

/// <summary>
/// The invited address already has an account, and acceptance needs that account to prove itself
/// rather than let the link's holder set a new password on it.
/// </summary>
public sealed class InvitationRequiresSignInException : Exception, IFailure
{
    public InvitationRequiresSignInException() : base("The invited address already has an account.") { }

    public FailureKind Kind => FailureKind.Unauthenticated;

    public string Code => FailureCodes.InvitationRequiresSignIn;

    public string? ClientMessage => "That address already has an account. Sign in, then open this link again.";
}

/// <summary>
/// An invitation was addressed to something with no '@' in it that no live account holds.
///
/// <para>Addresses and display names are both sign-in identifiers, and '@' is the line between them: no display name
/// may contain one, and sign-in looks an identifier without one up as a name. An invitation used to accept any text as
/// its address, so an account able to open a workspace could invite <c>admin</c> — or any other account's name — accept
/// it, and hold an account at an address that answered for that name.</para>
/// </summary>
public sealed class InvitationAddressNotAnEmailException : Exception, IFailure
{
    public InvitationAddressNotAnEmailException() : base("An invitation must be addressed to an email address containing '@'.") { }

    public FailureKind Kind => FailureKind.Invalid;

    public string Code => FailureCodes.InvitationAddressNotAnEmail;

    public string? ClientMessage => "Invite a full email address, like name@example.com.";
}

/// <summary>
/// A member tried to grant a role above their own. Granting upward would make every Admin an Owner
/// one invitation later.
/// </summary>
public sealed class InvitationRoleExceedsGranterException : Exception, IFailure
{
    public InvitationRoleExceedsGranterException(TeamRole requested, TeamRole granter) : base($"Cannot invite at {requested} while holding {granter}.")
    {
        Requested = requested;
        Granter = granter;
    }

    public TeamRole Requested { get; }
    public TeamRole Granter { get; }

    public FailureKind Kind => FailureKind.Forbidden;

    public string Code => FailureCodes.InvitationRoleExceedsGranter;

    /// <summary>
    /// Names the ceiling AND where it comes from. "You can't invite someone as Owner." leaves an Admin
    /// unable to tell their own rank from a team setting or a bug, so they retry at the same role; the
    /// granter's role is already on this exception and is a fact about the caller.
    /// </summary>
    public string? ClientMessage => $"Your role on this team is {Granter}, so you can invite up to {Granter}.";

    public IReadOnlyDictionary<string, object?>? Details => new Dictionary<string, object?> { ["yourRole"] = Granter.ToString(), ["requestedRole"] = Requested.ToString(), ["maximumGrantableRole"] = Granter.ToString() };
}

/// <summary>
/// The caller tried to regenerate or revoke an invitation outside their reach.
///
/// <para>An invitation IS the role it grants, waiting to be accepted — regenerating one hands the regenerator a
/// fresh working link to it — so regenerating is acting at that role: above your own rank never, at it only when it
/// is yours (an invitation you sent), unless you are an Owner. Without this an Admin regenerated a pending Owner
/// invitation, accepted it with an account of their own and was Owner a minute later. Revoking raises nobody, so it
/// is refused only above your own rank, and never for an invitation you sent.</para>
/// </summary>
public sealed class InvitationOutranksActorException : Exception, IFailure
{
    public InvitationOutranksActorException(TeamRole actor, TeamRole invitationRole) : base($"{actor} cannot manage this invitation at {invitationRole}.")
    {
        Actor = actor;
        InvitationRole = invitationRole;
    }

    public TeamRole Actor { get; }
    public TeamRole InvitationRole { get; }

    public FailureKind Kind => FailureKind.Forbidden;

    public string Code => FailureCodes.InvitationOutranksActor;

    /// <summary>Both roles, for the same reason <c>RoleOutranksActorException</c> names both: only the pair explains the refusal, and each is already visible to the caller on the invitation list.</summary>
    public string? ClientMessage => $"Your role on this team is {Actor}, so you can't change this {InvitationRole} invitation.";

    public IReadOnlyDictionary<string, object?>? Details => new Dictionary<string, object?> { ["yourRole"] = Actor.ToString(), ["invitationRole"] = InvitationRole.ToString() };
}

/// <summary>
/// A personal team is one person's own space — migration 0008 enforces one per user — so there is
/// nobody to invite into it.
/// </summary>
public sealed class PersonalTeamNotInvitableException : Exception, IFailure
{
    public PersonalTeamNotInvitableException() : base("A personal team cannot have members invited into it.") { }

    public FailureKind Kind => FailureKind.Unprocessable;

    public string Code => FailureCodes.PersonalTeamNotInvitable;

    public string? ClientMessage => "A personal workspace can't have other members. Create a team first.";
}

/// <summary>That address already holds a live invitation to this team; the unique index says so too.</summary>
public sealed class InvitationAlreadyPendingException : Exception, IFailure
{
    public InvitationAlreadyPendingException(string email) : base($"An invitation to {email} is already pending.") { Email = email; }

    public string Email { get; }

    public FailureKind Kind => FailureKind.Conflict;

    public string Code => FailureCodes.InvitationAlreadyPending;

    public string? ClientMessage => "That address already has a pending invitation. Revoke it first, or regenerate its link.";
}

/// <summary>The invited address is already a member; a second membership row would be meaningless.</summary>
public sealed class AlreadyTeamMemberException : Exception, IFailure
{
    public AlreadyTeamMemberException() : base("The user is already a member of this team.") { }

    public FailureKind Kind => FailureKind.Conflict;

    public string Code => FailureCodes.AlreadyTeamMember;

    public string? ClientMessage => "That person is already in this team.";
}
