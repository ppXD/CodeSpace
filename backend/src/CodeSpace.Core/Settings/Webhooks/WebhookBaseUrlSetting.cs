using Microsoft.Extensions.Configuration;

namespace CodeSpace.Core.Settings.Webhooks;

public class WebhookBaseUrlSetting : IConfigurationSetting<string>
{
    public WebhookBaseUrlSetting(IConfiguration configuration)
    {
        Value = configuration.GetValue<string>("Webhooks:BaseUrl") ?? "https://localhost";
    }

    public string Value { get; set; }
}
