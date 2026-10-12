using System.Globalization;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;

namespace CodeSpace.Core.Services.Auth;

/// <summary>
/// How long the API accepts a bearer token: until its <c>exp</c>, plus the clock skew the bearer handler tolerates.
/// Defined once because two places must agree on it — the bearer handler, which refuses an expired token at the door,
/// and a request that outlives the door (the run-ledger tail), which must stop serving the same token at the same moment.
/// </summary>
public static class BearerTokenLifetime
{
    /// <summary>How far past its <c>exp</c> a token is still accepted, to absorb clock drift between the issuing and the validating host.</summary>
    public static readonly TimeSpan ClockSkew = TimeSpan.FromMinutes(5);

    /// <summary>
    /// True while the bearer handler would still accept the principal's token at <paramref name="now"/>: up to and
    /// including its <c>exp</c> plus <see cref="ClockSkew"/>. False for a principal with no readable <c>exp</c>, which no
    /// token the handler lets through lacks.
    /// </summary>
    public static bool Accepts(ClaimsPrincipal? principal, DateTimeOffset now) => ExpiresAt(principal) is { } expiresAt && now <= expiresAt + ClockSkew;

    /// <summary>The principal's token <c>exp</c>, or null when it carries no readable one. Also how long a browser session cookie holding that token may live.</summary>
    public static DateTimeOffset? ExpiresAt(ClaimsPrincipal? principal) =>
        long.TryParse(principal?.FindFirst(JwtRegisteredClaimNames.Exp)?.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds) ? DateTimeOffset.FromUnixTimeSeconds(seconds) : null;
}
