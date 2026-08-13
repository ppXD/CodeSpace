using Microsoft.Extensions.Configuration;

namespace CodeSpace.Core.Settings.Webhooks;

/// <summary>
/// Where this API answers when GitHub or GitLab calls it — the origin webhook registration writes into the
/// provider, so the provider will keep it and deliver to it long after the request that registered it is gone.
///
/// <para>Outside Development a missing value is fatal. The built-in default is loopback, and a provider cannot
/// reach a loopback address: a deployment that started on it would register hooks that look healthy on both sides
/// and deliver nothing, and the failure surfaces as "pushes stopped triggering runs" weeks later rather than as a
/// startup error. Registering an undeliverable hook silently is worse than refusing to register one — the same
/// posture as <c>App:PublicBaseUrl</c>, for the same reason.</para>
/// </summary>
public class WebhookBaseUrlSetting : IConfigurationSetting<string>
{
    public const string ConfigurationKey = "Webhooks:BaseUrl";

    public const string DevelopmentDefault = "https://localhost";

    public WebhookBaseUrlSetting(IConfiguration configuration)
    {
        Value = Resolve(configuration);
    }

    public string Value { get; set; }

    private static string Resolve(IConfiguration configuration)
    {
        var configured = configuration[ConfigurationKey];

        if (!string.IsNullOrWhiteSpace(configured)) return configured.Trim().TrimEnd('/');

        var environment = configuration["ASPNETCORE_ENVIRONMENT"] ?? configuration["DOTNET_ENVIRONMENT"];

        if (string.IsNullOrWhiteSpace(environment) || string.Equals(environment, "Development", StringComparison.OrdinalIgnoreCase)) return DevelopmentDefault;

        throw new InvalidOperationException($"{ConfigurationKey} is required outside Development. It is the publicly reachable origin of this API — provider webhook registration writes it into GitHub/GitLab, and a hook registered on the loopback default can never be delivered to. Set Webhooks__BaseUrl=https://codespace-api.example.com.");
    }
}
