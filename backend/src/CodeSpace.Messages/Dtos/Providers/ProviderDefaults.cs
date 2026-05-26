using CodeSpace.Messages.Enums;

namespace CodeSpace.Messages.Dtos.Providers;

/// <summary>
/// What the frontend needs to render the "Add provider" form for a given provider kind:
/// the recommended base URL, the default OAuth scope list (so we never hard-code scope
/// strings in TypeScript), and the redirect URL the operator must paste into the provider's
/// app config. Driven entirely by <see cref="CodeSpace.Core.Services.Providers.Modules.IProviderModule"/>.
/// </summary>
public sealed record ProviderDefaults
{
    public required ProviderKind Provider { get; init; }
    public required string DefaultBaseUrl { get; init; }
    public required string DefaultDisplayName { get; init; }
    public required IReadOnlyList<string> DefaultOAuthScopes { get; init; }
    public required string OAuthCallbackUrl { get; init; }
}
