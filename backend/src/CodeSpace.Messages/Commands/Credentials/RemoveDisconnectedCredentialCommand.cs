using CodeSpace.Messages.Authorization;
using CodeSpace.Messages.Mediation;

namespace CodeSpace.Messages.Commands.Credentials;

/// <summary>
/// Takes a disconnected credential out of the team's list when nothing leans on it any more: a Disconnect leaves the row behind
/// (Revoked, its token blanked), and a person who disconnects and reconnects a few times is left with a pile of them — dead rows
/// that carry no repository, no group hook and no act-as identity, and that the Providers dialog could only list. The row is
/// soft-deleted, exactly the shape a departed member's retired credential has, so it is no longer listed and cannot be revived.
///
/// <para>Refused, and nothing written, for anything that is not exactly that: a credential that is still connected (a Disconnect, which
/// says where its repositories go, is the way to end one), a disconnected credential that still has repositories bound through it, group
/// hooks registered through it or an act-as identity backed by it (they are moved to another credential first, by
/// <c>RelinkCredentialBindingsCommand</c>), and one that was reconnected while the removal was running. A credential that is already
/// removed, or belongs to another team, reads as not found.</para>
///
/// <para><see cref="IRequireCredentialManagement"/>, the bar of every other write on one credential: its own person removes their own
/// Personal credential on membership alone; anyone else — a teammate's or a team-service credential — needs <c>credentials.manage</c>.</para>
/// </summary>
public sealed record RemoveDisconnectedCredentialCommand : ICommand<RemoveDisconnectedCredentialResult>, IRequireCredentialManagement
{
    /// <summary>Set by the controller from the route segment (URL is authoritative).</summary>
    public Guid CredentialId { get; init; }
}

public sealed record RemoveDisconnectedCredentialResult
{
    public required Guid CredentialId { get; init; }
}
