using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Messages.Enums;

namespace CodeSpace.Core.Services.OAuth;

/// <summary>
/// Per-provider OAuth flow. Adding a new provider is one new IOAuthClient implementation —
/// the resolver / controller / commands stay generic. PKCE (S256) is mandatory on every
/// implementation; the verifier flows from <see cref="OAuthPendingState.CodeVerifier"/>.
/// </summary>
public interface IOAuthClient
{
    ProviderKind Kind { get; }

    Uri BuildAuthorizeUrl(OAuthAuthorizeInput input);

    Task<OAuthTokenResponse> ExchangeCodeAsync(OAuthCodeExchangeInput input, CancellationToken cancellationToken);

    Task<OAuthTokenResponse> RefreshAsync(OAuthRefreshInput input, CancellationToken cancellationToken);

    /// <summary>
    /// Asks the provider to revoke the given token. MUST be idempotent on the caller side —
    /// if the provider says "already revoked" or returns a hard failure, the implementation
    /// returns normally so the surrounding code can still mark the local credential as
    /// revoked. Network / 5xx errors should propagate so the caller can decide whether to
    /// retry (handler treats provider revoke as best-effort).
    /// </summary>
    Task RevokeAsync(OAuthRevokeInput input, CancellationToken cancellationToken);
}

public sealed record OAuthAuthorizeInput
{
    public required ProviderInstance Instance { get; init; }
    public required string ClientId { get; init; }
    public required string State { get; init; }
    public required string CodeChallengeS256 { get; init; }
    public required Uri RedirectUri { get; init; }
    public IReadOnlyList<string>? Scopes { get; init; }
}

public sealed record OAuthCodeExchangeInput
{
    public required ProviderInstance Instance { get; init; }
    public required string ClientId { get; init; }
    public required string ClientSecret { get; init; }
    public required string Code { get; init; }
    public required string CodeVerifier { get; init; }
    public required Uri RedirectUri { get; init; }
}

public sealed record OAuthRefreshInput
{
    public required ProviderInstance Instance { get; init; }
    public required string ClientId { get; init; }
    public required string ClientSecret { get; init; }
    public required string RefreshToken { get; init; }
}

public sealed record OAuthRevokeInput
{
    public required ProviderInstance Instance { get; init; }
    public required string ClientId { get; init; }
    public required string ClientSecret { get; init; }
    public required string Token { get; init; }

    /// <summary>"access_token" or "refresh_token" — providers use this only as a hint.</summary>
    public string? TokenTypeHint { get; init; }
}

public sealed record OAuthTokenResponse
{
    public required string AccessToken { get; init; }
    public string? RefreshToken { get; init; }
    public DateTimeOffset? ExpiresAt { get; init; }
    public IReadOnlyList<string>? GrantedScopes { get; init; }
    public string? TokenType { get; init; }
}
