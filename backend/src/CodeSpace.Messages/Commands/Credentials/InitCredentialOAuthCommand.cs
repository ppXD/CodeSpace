using CodeSpace.Messages.Authorization;
using CodeSpace.Messages.Mediation;

namespace CodeSpace.Messages.Commands.Credentials;

/// <summary>
/// Begins a provider-credential OAuth flow. Returns the authorize URL the SPA should open
/// in a popup or full redirect. The provider's callback hits <c>CompleteCredentialOAuthCommand</c>.
/// </summary>
public sealed record InitCredentialOAuthCommand : ICommand<InitCredentialOAuthResult>, IRequireTeamMembership
{
    /// <summary>Provider instance we're authorising against. Must belong to the current team.</summary>
    public required Guid ProviderInstanceId { get; init; }

    /// <summary>Human-friendly name for the resulting credential row (e.g. "Maya's GitLab OAuth").</summary>
    public required string DisplayName { get; init; }

    /// <summary>
    /// User who will own the resulting credential. NULL = team-shared (only Admins can set
    /// this). Defaults to the initiating user (set by the handler if absent).
    /// </summary>
    public Guid? IntendedOwnerUserId { get; init; }

    /// <summary>Where to redirect the browser after success. Opaque to the backend; passed through verbatim with query params appended.</summary>
    public string? ReturnUrl { get; init; }

    /// <summary>Override the provider_instance's default scope list. Usually null.</summary>
    public IReadOnlyList<string>? Scopes { get; init; }
}

public sealed record InitCredentialOAuthResult
{
    public required Uri AuthorizeUrl { get; init; }
    public required string State { get; init; }
}
