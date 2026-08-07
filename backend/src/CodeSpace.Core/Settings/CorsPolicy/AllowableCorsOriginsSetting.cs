using Microsoft.Extensions.Configuration;

namespace CodeSpace.Core.Settings.CorsPolicy;

/// <summary>
/// Origins allowed to call this API cross-origin — the SPA's host when it talks to the backend directly instead of
/// through the Vite dev proxy (which is same-origin, so CORS is a no-op there).
///
/// <para>Accepts BOTH shapes the configuration pipeline can deliver, because a ConfigMap and a JSON file naturally
/// express a list differently: a JSON array (<c>Cors:AllowedOrigins:0</c>, …) or one comma-separated string
/// (<c>Cors__AllowedOrigins=https://a,https://b</c>). A ConfigMap or Helm value can only easily produce the latter,
/// and reading only the array form would leave such a deployment with an empty allow-list — every cross-origin call
/// failing, with nothing in the logs pointing at the config.</para>
/// </summary>
public class AllowableCorsOriginsSetting : IConfigurationSetting<string[]>
{
    public const string ConfigurationKey = "Cors:AllowedOrigins";

    public AllowableCorsOriginsSetting(IConfiguration configuration)
    {
        Value = Resolve(configuration);
    }

    public string[] Value { get; set; }

    private static string[] Resolve(IConfiguration configuration)
    {
        var array = configuration.GetSection(ConfigurationKey).Get<string[]>();

        if (array is { Length: > 0 }) return Clean(array);

        return Clean((configuration[ConfigurationKey] ?? string.Empty).Split(','));
    }

    /// <summary>A trailing slash makes an origin never match (the browser sends a bare scheme+host+port), so trim it here rather than leaving an operator to discover it from a failed preflight.</summary>
    private static string[] Clean(IEnumerable<string> origins) =>
        origins.Select(o => o.Trim().TrimEnd('/')).Where(o => o.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
}
