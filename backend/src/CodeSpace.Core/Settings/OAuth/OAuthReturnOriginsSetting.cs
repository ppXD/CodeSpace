using CodeSpace.Core.Settings.Application;
using CodeSpace.Core.Settings.CorsPolicy;
using Microsoft.Extensions.Configuration;

namespace CodeSpace.Core.Settings.OAuth;

/// <summary>
/// The origins an OAuth connect may send the browser back to — every origin configuration says this deployment's web app
/// answers on: <c>App:PublicBaseUrl</c> when it is set, every <c>Cors:AllowedOrigins</c> entry (an SPA that calls the API
/// directly is listed there, or it could not call at all), the origin of <c>OAuth:CallbackUrl</c> (the provider already
/// sends the browser to that host, and in a same-origin deployment it is where the SPA answers), and in Development the
/// dev SPA origins the CORS policy adds.
///
/// <para>Never throws. <see cref="PublicBaseUrlSetting"/> does outside Development when its key is unset — rightly: a
/// link minted for someone else must open somewhere real. A connect is different. It returns to the browser that started
/// it, usually by a path that needs no origin at all, so an unset key only narrows what an absolute return address may
/// name. Injecting the throwing setting into the OAuth service made every connect answer 500 — path or not, start or
/// callback — on a host that never set the key, the shipped docker-compose API among them.</para>
/// </summary>
public class OAuthReturnOriginsSetting : IConfigurationSetting<string[]>
{
    public OAuthReturnOriginsSetting(IConfiguration configuration)
    {
        Value = Resolve(configuration);
    }

    public string[] Value { get; set; }

    private static string[] Resolve(IConfiguration configuration) =>
        PublicBaseUrl(configuration)
            .Concat(new AllowableCorsOriginsSetting(configuration).Value)
            .Concat(CallbackOrigin(configuration))
            .Concat(IsDevelopment(configuration) ? AllowableCorsOriginsSetting.DevelopmentOrigins : Array.Empty<string>())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

    /// <summary>The raw key, read the way <see cref="PublicBaseUrlSetting"/> normalises it, but with no fallback and no throw.</summary>
    private static IEnumerable<string> PublicBaseUrl(IConfiguration configuration)
    {
        var configured = configuration[PublicBaseUrlSetting.ConfigurationKey];

        return string.IsNullOrWhiteSpace(configured) ? Array.Empty<string>() : new[] { configured.Trim().TrimEnd('/') };
    }

    private static IEnumerable<string> CallbackOrigin(IConfiguration configuration)
    {
        var callbackUrl = new OAuthCallbackUrlSetting(configuration).Value;

        if (!Uri.TryCreate(callbackUrl, UriKind.Absolute, out var callback) || (callback.Scheme != Uri.UriSchemeHttps && callback.Scheme != Uri.UriSchemeHttp)) return Array.Empty<string>();

        return new[] { callback.GetLeftPart(UriPartial.Authority) };
    }

    /// <summary>The same reading of the environment <see cref="PublicBaseUrlSetting"/> makes, so the two never disagree about what Development is.</summary>
    private static bool IsDevelopment(IConfiguration configuration)
    {
        var environment = configuration["ASPNETCORE_ENVIRONMENT"] ?? configuration["DOTNET_ENVIRONMENT"];

        return string.IsNullOrWhiteSpace(environment) || string.Equals(environment, "Development", StringComparison.OrdinalIgnoreCase);
    }
}
