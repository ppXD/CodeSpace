using System.Text;
using CodeSpace.Core.Settings.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.IdentityModel.Tokens;

namespace CodeSpace.Api.Extensions;

public static class AuthenticationExtension
{
    /// <summary>Minimum entropy for the JWT symmetric key. Below this, HS256 is brute-forceable. Pinned by unit test.</summary>
    public const int MinKeyByteLength = 32;

    public static void AddCustomAuthentication(this IServiceCollection services, IConfiguration configuration, IHostEnvironment environment)
    {
        var jwtKey = new JwtSymmetricKeySetting(configuration).Value;

        EnsureKeyIsPresent(jwtKey);
        EnsureKeyIsStrong(jwtKey);

        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(options =>
            {
                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateLifetime = true,
                    ValidateAudience = false,
                    ValidateIssuer = false,
                    ValidateIssuerSigningKey = true,
                    ClockSkew = TimeSpan.FromMinutes(5),
                    IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtKey))
                };
            });

        services.AddAuthorization(options =>
        {
            var policy = new AuthorizationPolicyBuilder(JwtBearerDefaults.AuthenticationScheme).RequireAuthenticatedUser().Build();
            options.DefaultPolicy = policy;
            // FallbackPolicy is the critical bit: it applies to endpoints WITHOUT an explicit [Authorize].
            // Without this, missing [Authorize] silently means anonymous access — the v1 P0 footgun.
            options.FallbackPolicy = policy;
        });
    }

    /// <summary>
    /// A missing key is fatal in EVERY environment. There used to be an env escape hatch that let a non-Production
    /// host boot with every endpoint anonymous; it is gone, because a run-with-no-auth switch is exactly the kind of
    /// deployment-time toggle this codebase does not keep — and it was never needed, since appsettings.json ships a
    /// committed development key. Blanking that key is now a loud failure rather than a silent slide into anonymous.
    /// </summary>
    private static void EnsureKeyIsPresent(string jwtKey)
    {
        if (!string.IsNullOrWhiteSpace(jwtKey)) return;

        throw new InvalidOperationException("Authentication:Jwt:SymmetricKey is required. Set it in appsettings, a ConfigMap, or the Authentication__Jwt__SymmetricKey environment variable.");
    }

    private static void EnsureKeyIsStrong(string jwtKey)
    {
        var byteLength = Encoding.UTF8.GetByteCount(jwtKey);
        if (byteLength < MinKeyByteLength) throw new InvalidOperationException($"Authentication:Jwt:SymmetricKey must be at least {MinKeyByteLength} bytes of entropy (got {byteLength}). HS256 is brute-forceable below that threshold.");
    }
}
