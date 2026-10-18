using CodeSpace.Messages.Authorization;
using CodeSpace.Messages.Mediation;
using MediatR;

namespace CodeSpace.Messages.Commands.Identity;

/// <summary>
/// Unlink one of the caller's OWN provider identities (soft-delete + clear token material). The credential behind it ends
/// exactly as a Disconnect ends one, so the repositories and group hooks bound through it follow the person's one other
/// live connection on that provider instance when they hold exactly one, and turn "needs a new credential" otherwise.
/// </summary>
public sealed record UnlinkProviderIdentityCommand : ICommand<Unit>, IRequireTeamMembership
{
    public required Guid IdentityId { get; init; }
}
