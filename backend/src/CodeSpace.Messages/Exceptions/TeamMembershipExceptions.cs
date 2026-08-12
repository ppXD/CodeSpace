using CodeSpace.Messages.Enums;
using CodeSpace.Messages.Failures;

namespace CodeSpace.Messages.Exceptions;

/// <summary>
/// The last Owner cannot be demoted, removed, or allowed to leave.
///
/// <para>A team with no owner has no one who can transfer ownership, invite an owner, or delete it —
/// it is not a degraded state, it is an unrecoverable one. Transferring first is the way out, which
/// is why that is a distinct action rather than a side effect of changing a role.</para>
/// </summary>
public sealed class LastOwnerException : Exception, IFailure
{
    public LastOwnerException() : base("A team must always have at least one owner.") { }

    public FailureKind Kind => FailureKind.Conflict;

    public string Code => FailureCodes.LastOwner;

    public string? ClientMessage => "A team must always have an owner. Transfer ownership first.";
}

/// <summary>
/// The actor tried to act on someone at or above their own standing, or to grant a role above it.
///
/// <para>One exception for both directions because they are the same rule: you may not reach past
/// your own rank. Without it an Admin demotes the Owner, or promotes themselves by editing their own
/// row.</para>
/// </summary>
public sealed class RoleOutranksActorException : Exception, IFailure
{
    public RoleOutranksActorException(TeamRole actor, TeamRole subject) : base($"A {actor} cannot act on a {subject}.")
    {
        Actor = actor;
        Subject = subject;
    }

    public TeamRole Actor { get; }
    public TeamRole Subject { get; }

    public FailureKind Kind => FailureKind.Forbidden;

    public string Code => FailureCodes.RoleOutranksActor;

    public string? ClientMessage => $"You can't do that to someone who is {Subject}.";
}
