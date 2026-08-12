using Microsoft.Extensions.Configuration;

namespace CodeSpace.Core.Settings.Invitations;

/// <summary>
/// Shared resolution for the two settings that produce a link a human has to be able to open.
/// </summary>
public abstract class LinkTemplateSetting
{
    /// <summary>
    /// A link template that still points at the dev SPA is useless in production: the person who
    /// receives it cannot open it, and nothing says so — the invitation looks sent and the reset
    /// looks issued.
    ///
    /// <para>So a non-Development host refuses to start on the default rather than minting links
    /// nobody can follow. Same posture as the JWT key, and for the same reason: this codebase does not
    /// keep switches that let a misconfigured deployment run in a quietly broken state.</para>
    /// </summary>
    protected static string ResolveOrFail(IConfiguration configuration, string key, string developmentDefault, string settingName)
    {
        var configured = configuration[key];

        if (!string.IsNullOrWhiteSpace(configured)) return EnsureTokenPlaceholder(configured.Trim(), key);

        var environment = configuration["ASPNETCORE_ENVIRONMENT"] ?? configuration["DOTNET_ENVIRONMENT"];

        if (string.Equals(environment, "Development", StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(environment)) return developmentDefault;

        throw new InvalidOperationException($"{key} is required outside Development. {settingName} are handed to people who must be able to open them, and the built-in default points at the development SPA. Set it to the origin your SPA answers on, e.g. {key.Replace(':', '_').Replace('_', '_')}=https://codespace.example.com/... with {{token}} where the token goes.");
    }

    private static string EnsureTokenPlaceholder(string template, string key)
    {
        if (!template.Contains("{token}", StringComparison.Ordinal)) throw new InvalidOperationException($"{key} must contain the literal {{token}} placeholder — without it every link generated is the same URL and none of them carry a token.");

        return template;
    }
}
