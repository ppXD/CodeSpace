using CodeSpace.Core.Services.Identity;
using CodeSpace.Core.Settings.CorsPolicy;
using Serilog;

namespace CodeSpace.Api.Extensions;

/// <summary>
/// The SPA cross-origin policy, as ONE named policy built from <see cref="AllowableCorsOriginsSetting"/>. It used to
/// be assembled inline in <c>Startup.ConfigureServices</c>, which put a configuration read, a list mutation and the
/// policy build in the middle of service registration; here the whole policy is one readable unit and the origins are
/// a normal setting like everything else.
/// </summary>
public static class CorsPolicyExtension
{
    /// <summary>Named rather than the default policy so <c>UseCors</c> states which policy it is applying, and a second policy (an admin surface, a webhook origin) can be added later without silently widening this one.</summary>
    public const string PolicyName = "CodeSpaceSpa";

    /// <summary>The Vite dev-server origins, added in Development only. The port is pinned by vite.config.ts — keep these in sync.</summary>
    private static readonly string[] DevelopmentOrigins = { "http://localhost:5180", "http://127.0.0.1:5180" };

    public static IServiceCollection AddCorsPolicy(this IServiceCollection services, IConfiguration configuration, IHostEnvironment environment)
    {
        var origins = ResolveOrigins(configuration, environment);

        Log.Information(
            "CORS policy {CorsPolicyName} resolved for {EnvironmentName}; allowed origins: {@AllowedOrigins}",
            PolicyName,
            environment.EnvironmentName,
            origins);

        services.AddCors(options => options.AddPolicy(PolicyName, policy => policy
            .WithOrigins(origins)
            .AllowAnyHeader()      // the SPA sends Authorization + X-Team-Id + Content-Type
            .AllowAnyMethod()      // GET/POST/PATCH/DELETE + the OPTIONS preflight
            // AllowAnyHeader governs REQUEST headers only; a custom RESPONSE header stays unreadable cross-origin
            // unless it is exposed. The SPA reads the current-team header off a response, so expose it by the
            // constant the server writes it with — the exposed name can then never drift from the emitted one.
            .WithExposedHeaders(HeaderCurrentTeam.HeaderName)
            .AllowCredentials()));  // no cookies today, but keeps a refresh-cookie flow open

        return services;
    }

    /// <summary>
    /// Configured origins, plus the Vite dev origins in Development so the plain "fresh clone → pnpm dev → dotnet run"
    /// flow needs no extra setup. Internal so the Development-only widening is unit-pinned: a bug that leaked the dev
    /// origins into Production would be an invisible hole in the allow-list.
    /// </summary>
    internal static string[] ResolveOrigins(IConfiguration configuration, IHostEnvironment environment)
    {
        var configured = new AllowableCorsOriginsSetting(configuration).Value;

        if (!environment.IsDevelopment()) return configured;

        return configured.Concat(DevelopmentOrigins).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }
}
