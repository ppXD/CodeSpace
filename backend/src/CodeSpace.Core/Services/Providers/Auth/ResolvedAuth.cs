namespace CodeSpace.Core.Services.Providers.Auth;

/// <summary>
/// Output of an auth strategy. Token is the canonical credential the SDK consumes; ExpiresAt
/// lets callers cache safely (null = no expiry / caller-defined); ExtraHeaders lets a future
/// GitHub-App strategy attach Installation-ID headers without leaking that detail into the
/// provider class.
/// </summary>
public sealed record ResolvedAuth
{
    public required string Token { get; init; }
    public DateTimeOffset? ExpiresAt { get; init; }
    public IReadOnlyDictionary<string, string>? ExtraHeaders { get; init; }
}
