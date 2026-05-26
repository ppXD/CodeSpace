using CodeSpace.Messages.Enums;

namespace CodeSpace.Messages.Dtos.ProviderInstances;

public sealed record ProviderInstanceSummary
{
    public required Guid Id { get; init; }
    public required Guid TeamId { get; init; }
    public required ProviderKind Provider { get; init; }
    public required string DisplayName { get; init; }
    public required string BaseUrl { get; init; }
    public string? ApiUrl { get; init; }
    public string? WebUrl { get; init; }
    public required DateTimeOffset CreatedDate { get; init; }

    /// <summary>
    /// True when this instance has OAuth client_id + client_secret configured. Frontend uses
    /// this to enable/disable the "Connect via OAuth" affordance — without these fields the
    /// init command throws OAuthCallbackException.
    /// </summary>
    public required bool OauthEnabled { get; init; }
}
