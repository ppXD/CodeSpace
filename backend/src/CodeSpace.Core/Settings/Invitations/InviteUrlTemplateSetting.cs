using Microsoft.Extensions.Configuration;

namespace CodeSpace.Core.Settings.Invitations;

/// <summary>
/// The shape of the link a member sends an invitee, with <c>{token}</c> where the token goes.
///
/// <para>Configuration rather than a constant because only the deployment knows where its SPA is
/// answering: the dev proxy, a separate origin in production, a preview host. Building it from the
/// inbound request instead would let anyone who can set a Host header mint an invitation pointing at
/// their own site — the invitee would then type a new password into it.</para>
///
/// <para>Not a feature toggle: the value is a coordinate, and every deployment has exactly one right
/// answer for it. The default is the dev SPA, so a fresh clone works without configuring anything.</para>
/// </summary>
public class InviteUrlTemplateSetting : LinkTemplateSetting, IConfigurationSetting<string>
{
    public const string ConfigurationKey = "Invitations:InviteUrlTemplate";

    public const string DefaultTemplate = "http://localhost:5180/invite/{token}";

    public InviteUrlTemplateSetting(IConfiguration configuration)
    {
        Value = ResolveOrFail(configuration, ConfigurationKey, DefaultTemplate, "Invite links");
    }

    public string Value { get; set; }
}
