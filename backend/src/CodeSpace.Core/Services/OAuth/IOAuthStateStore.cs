using CodeSpace.Core.Persistence.Entities;

namespace CodeSpace.Core.Services.OAuth;

/// <summary>
/// Persists OAuth flow context between /init and /callback. The state value is the row's
/// primary key; treat it as both a CSRF token and a lookup key. Consume deletes the row —
/// one-time use is enforced by the unique PK + delete combo.
/// </summary>
public interface IOAuthStateStore
{
    Task<OAuthPendingState> CreateAsync(OAuthPendingStateInput input, CancellationToken cancellationToken);

    /// <summary>
    /// Looks up + deletes the row in one logical step — but only the row whose state AND browser-nonce hash both
    /// match. Returns null if no such row exists or it has expired (an expired match is also deleted as cleanup
    /// hygiene). A state presented without its browser's nonce matches nothing, so it consumes nothing: the
    /// browser that started the connect can still finish it.
    /// </summary>
    Task<OAuthPendingState?> ConsumeAsync(string state, string browserNonceHash, CancellationToken cancellationToken);
}

public sealed record OAuthPendingStateInput
{
    public required Guid ProviderInstanceId { get; init; }
    public required Guid TeamId { get; init; }
    public required Guid InitiatorUserId { get; init; }
    public required string CodeVerifier { get; init; }
    public required string IntendedDisplayName { get; init; }
    public Guid? IntendedOwnerUserId { get; init; }
    public string? ReturnUrl { get; init; }
    public required string BrowserNonceHash { get; init; }
    public IReadOnlyList<string>? RequestedScopes { get; init; }
    public required TimeSpan Ttl { get; init; }
}
