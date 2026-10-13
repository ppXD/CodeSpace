using CodeSpace.Api.Http;
using CodeSpace.Core.Services.Identity;
using CodeSpace.Core.Settings.CorsPolicy;

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

    public static IServiceCollection AddCorsPolicy(this IServiceCollection services, IConfiguration configuration, IHostEnvironment environment)
    {
        var origins = ResolveOrigins(configuration, environment);

        services.AddCors(options => options.AddPolicy(PolicyName, policy => policy
            .WithOrigins(origins)
            .AllowAnyHeader()      // the SPA sends X-CodeSpace-Csrf + X-Team-Id + Content-Type (+ Authorization while on a pre-cookie token)
            .AllowAnyMethod()      // GET/POST/PATCH/DELETE + the OPTIONS preflight
            // AllowAnyHeader governs REQUEST headers only; a custom RESPONSE header stays unreadable cross-origin
            // unless it is exposed. The SPA reads the current-team, bounded log-range, and bounded event-data contracts, so expose them
            // from the same constants the server emits — the readable names can never drift from the written names.
            .WithExposedHeaders([HeaderCurrentTeam.HeaderName, .. AgentRunLogHttpHeaders.RangeResponseHeaders, .. AgentRunEventDataHttpHeaders.RangeResponseHeaders, .. RunRecordPayloadHttpHeaders.RangeResponseHeaders])
            .AllowCredentials()));  // the browser session is a cookie (SessionCookie): without this a cross-origin SPA neither sends it nor keeps it

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

        return configured.Concat(AllowableCorsOriginsSetting.DevelopmentOrigins).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }
}
