using CodeSpace.Messages.Authorization;
using CodeSpace.Messages.Dtos.Providers;
using CodeSpace.Messages.Mediation;
using MediatR;

namespace CodeSpace.Messages.Commands.Identity;

/// <summary>
/// Link the caller's OWN identity on a provider instance via a personal access token (Model B).
/// The token is probed (whoami) before anything persists, so a bad token never writes a row.
/// Requires team membership — the provider instance is team-scoped.
/// </summary>
public sealed record LinkProviderIdentityByPatCommand : ICommand<UserProviderIdentitySummary>, IRequireTeamMembership
{
    public required Guid ProviderInstanceId { get; init; }

    /// <summary>The personal access token. Carried in the request BODY only; never logged or echoed back.</summary>
    public required string AccessToken { get; init; }
}
