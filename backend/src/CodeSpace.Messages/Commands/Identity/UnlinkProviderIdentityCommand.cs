using CodeSpace.Messages.Authorization;
using MediatR;

namespace CodeSpace.Messages.Commands.Identity;

/// <summary>Unlink one of the caller's OWN provider identities (soft-delete + clear token material).</summary>
public sealed record UnlinkProviderIdentityCommand : IRequest, IRequireTeamMembership
{
    public required Guid IdentityId { get; init; }
}
