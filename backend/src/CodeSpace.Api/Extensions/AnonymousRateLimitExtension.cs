using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;

namespace CodeSpace.Api.Extensions;

/// <summary>
/// A per-caller ceiling on the endpoints anyone can reach without a session.
///
/// <para>Sign-in, invitation acceptance and password reset are the three surfaces that answer to an
/// unauthenticated stranger, and each one grades a secret: a password, an invitation token, a reset
/// token. Without a ceiling, the only thing standing between a guesser and an account is how fast
/// they can send requests — which for a 256-bit token is fine and for a password is not.</para>
///
/// <para>Partitioned by remote address rather than globally: one noisy client must not be able to
/// lock everyone else out of signing in, which would turn a rate limit into the outage it exists to
/// prevent.</para>
/// </summary>
public static class AnonymousRateLimitExtension
{
    public const string PolicyName = "anonymous-auth";

    /// <summary>Generous for a person, useless for a script. A human signs in wrong three times, not thirty.</summary>
    public const int PermitsPerWindow = 10;

    public static readonly TimeSpan Window = TimeSpan.FromMinutes(1);

    public static void AddAnonymousRateLimit(this IServiceCollection services)
    {
        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

            options.AddPolicy(PolicyName, context => RateLimitPartition.GetFixedWindowLimiter(
                context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                _ => new FixedWindowRateLimiterOptions { PermitLimit = PermitsPerWindow, Window = Window, QueueLimit = 0 }));
        });
    }
}
