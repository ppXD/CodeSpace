using Microsoft.Extensions.Configuration;

namespace CodeSpace.Core.Settings.Invitations;

/// <summary>
/// Where the SPA answers a password-reset link, with <c>{token}</c> where the token goes.
///
/// <para>Configuration for the same reason the invite template is: building it from the inbound
/// request's Host header would let anyone who can set one mint a link pointing at their own site,
/// and a person following a reset link is about to type a new password into whatever it opens.</para>
/// </summary>
public class ResetUrlTemplateSetting : LinkTemplateSetting, IConfigurationSetting<string>
{
    public const string ConfigurationKey = "Invitations:ResetUrlTemplate";

    public const string DefaultTemplate = "http://localhost:5180/reset-password/{token}";

    public ResetUrlTemplateSetting(IConfiguration configuration)
    {
        Value = ResolveOrFail(configuration, ConfigurationKey, DefaultTemplate, "Password reset links");
    }

    public string Value { get; set; }
}
