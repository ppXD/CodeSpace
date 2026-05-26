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

    /// <summary>If you really want to run without auth in Development, set this env var to true. Production ignores it.</summary>
    public const string AllowAnonymousFallbackEnvVar = "CODESPACE_ALLOW_ANONYMOUS_FALLBACK";

    public static void AddCustomAuthentication(this IServiceCollection services, IConfiguration configuration, IHostEnvironment environment)
    {
        var jwtKey = new JwtSymmetricKeySetting(configuration).Value;

        if (string.IsNullOrWhiteSpace(jwtKey))
        {
            EnsureMissingKeyIsAllowed(environment);
            return;
        }

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

    private static void EnsureMissingKeyIsAllowed(IHostEnvironment environment)
    {
        if (environment.IsProduction()) throw new InvalidOperationException("Authentication:Jwt:SymmetricKey is required in Production. Set the configuration value or environment variable.");

        var allowAnonymous = Environment.GetEnvironmentVariable(AllowAnonymousFallbackEnvVar);
        if (!string.Equals(allowAnonymous, "true", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Authentication:Jwt:SymmetricKey is missing. Set the key, or export {AllowAnonymousFallbackEnvVar}=true to explicitly run anonymous in {environment.EnvironmentName}.");

        Console.WriteLine($"[WARN] {AllowAnonymousFallbackEnvVar}=true — every endpoint is anonymous. NEVER set this in Production.");
    }

    private static void EnsureKeyIsStrong(string jwtKey)
    {
        var byteLength = Encoding.UTF8.GetByteCount(jwtKey);
        if (byteLength < MinKeyByteLength) throw new InvalidOperationException($"Authentication:Jwt:SymmetricKey must be at least {MinKeyByteLength} bytes of entropy (got {byteLength}). HS256 is brute-forceable below that threshold.");
    }
}
