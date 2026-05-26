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
    /// Looks up + deletes the row in one logical step. Returns null if the row doesn't exist
    /// or has expired (the expired row is also deleted as a cleanup hygiene).
    /// </summary>
    Task<OAuthPendingState?> ConsumeAsync(string state, CancellationToken cancellationToken);
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
    public IReadOnlyList<string>? RequestedScopes { get; init; }
    public required TimeSpan Ttl { get; init; }
}
