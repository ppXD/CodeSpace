using CodeSpace.Messages.Enums;

namespace CodeSpace.Core.Services.Credentials;

/// <summary>
/// What to read off a credential, from what is recorded about it — computed when it is listed, never stored, so it cannot
/// disagree with the columns it is derived from. Only a static token (a personal, project or group access token) has an
/// expiry worth showing: an OAuth credential's <c>expires_date</c> is its access token's, which renews itself every couple
/// of hours, and reading it as "expiring" would flag every healthy connection.
/// </summary>
public static class CredentialHealthDerivation
{
    /// <summary>How long before a static token expires it reads <see cref="CredentialHealth.Expiring"/>. GitLab caps a token's life at a year, so a month is a warning a person can act on.</summary>
    public static readonly TimeSpan ExpiringWithin = TimeSpan.FromDays(30);

    /// <summary>
    /// Disconnected beats everything (there is nothing left to be healthy); a provider's refusal — recorded, or an expiry that
    /// has passed — beats a departed owner (it needs reconnecting whoever holds it); then a token about to expire; then fine.
    /// </summary>
    public static CredentialHealth Derive(CredentialStatus status, AuthType authType, DateTimeOffset? expiresDate, bool ownerLeft, DateTimeOffset now)
    {
        if (status == CredentialStatus.Revoked) return CredentialHealth.Revoked;
        if (status is CredentialStatus.Error or CredentialStatus.Expired) return CredentialHealth.Rejected;
        if (HasExpired(authType, expiresDate, now)) return CredentialHealth.Rejected;
        if (ownerLeft) return CredentialHealth.OwnerLeft;

        return IsExpiringSoon(authType, expiresDate, now) ? CredentialHealth.Expiring : CredentialHealth.Active;
    }

    /// <summary>Whole days left before a static token expires (0 once it has); null for any other credential, and for a token with no recorded expiry.</summary>
    public static int? DaysToExpiry(AuthType authType, DateTimeOffset? expiresDate, DateTimeOffset now) =>
        IsStaticToken(authType) && expiresDate is { } at ? (int)Math.Max(0, Math.Floor((at - now).TotalDays)) : null;

    /// <summary>The credentials whose provider-side expiry CodeSpace records: access tokens a person created, not tokens the provider issued for a grant.</summary>
    public static bool IsStaticToken(AuthType authType) => authType is AuthType.Pat or AuthType.ProjectAccessToken or AuthType.GroupAccessToken;

    private static bool HasExpired(AuthType authType, DateTimeOffset? expiresDate, DateTimeOffset now) => IsStaticToken(authType) && expiresDate is { } at && at <= now;

    private static bool IsExpiringSoon(AuthType authType, DateTimeOffset? expiresDate, DateTimeOffset now) => IsStaticToken(authType) && expiresDate is { } at && at - now <= ExpiringWithin;
}
