using CodeSpace.Messages.Authorization;
using CodeSpace.Messages.Enums;
using CodeSpace.Messages.Mediation;

namespace CodeSpace.Messages.Commands.ProviderInstances;

public sealed record AddProviderInstanceCommand : ICommand<Guid>, IRequireTeamMembership
{
    public required ProviderKind Provider { get; init; }
    public required string DisplayName { get; init; }
    public required string BaseUrl { get; init; }
    public string? ApiUrl { get; init; }
    public string? WebUrl { get; init; }
    public string? OauthClientId { get; init; }
    public string? OauthClientSecret { get; init; }
    public string? OauthRedirectPath { get; init; }
    public IReadOnlyList<string>? OauthDefaultScopes { get; init; }
}
