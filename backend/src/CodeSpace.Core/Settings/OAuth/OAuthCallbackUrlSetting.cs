using Microsoft.Extensions.Configuration;

namespace CodeSpace.Core.Settings.OAuth;

/// <summary>
/// Absolute URL the OAuth provider redirects users back to after authorization. MUST exactly
/// match the redirect URI registered with each provider OAuth app — providers reject the
/// exchange on the smallest mismatch (scheme, port, trailing slash).
///
/// Read from config key `OAuth:CallbackUrl`. Example values:
///   • Dev:   http://localhost:5000/api/credentials/oauth/callback
///   • Prod:  https://app.codespace.dev/api/credentials/oauth/callback
///
/// A single global callback handles every provider; the per-flow state token tells the
/// callback which provider_instance the code is for.
/// </summary>
public class OAuthCallbackUrlSetting : IConfigurationSetting<string?>
{
    public OAuthCallbackUrlSetting(IConfiguration configuration)
    {
        Value = configuration.GetValue<string?>("OAuth:CallbackUrl");
    }

    public string? Value { get; set; }
}
