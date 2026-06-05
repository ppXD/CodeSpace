using CodeSpace.Messages.Authorization;
using CodeSpace.Messages.Mediation;
using MediatR;

namespace CodeSpace.Messages.Commands.Identity;

/// <summary>Unlink one of the caller's OWN provider identities (soft-delete + clear token material).</summary>
public sealed record UnlinkProviderIdentityCommand : ICommand<Unit>, IRequireTeamMembership
{
    public required Guid IdentityId { get; init; }
}
