using Microsoft.Extensions.Configuration;

namespace CodeSpace.Core.Settings.Application;

/// <summary>
/// Where this deployment's SPA answers — the one coordinate the server needs in order to hand a
/// person a link they can open.
///
/// <para>One setting rather than a template per link type. The paths (<c>/invite/…</c>,
/// <c>/reset-password/…</c>) are the frontend's routes, which is OUR code, not an operator's choice;
/// asking someone to restate them in configuration invites a deployment where the invite path is
/// right and the reset path is a typo, and nothing notices until a locked-out person follows a dead
/// link.</para>
///
/// <para>Deliberately not built from the inbound request's Host header: anyone who can set one could
/// then mint a link pointing at their own site, and the recipient is about to type a new password
/// into whatever it opens.</para>
///
/// <para>Outside Development a missing value is fatal. The built-in default is the dev SPA, so a
/// production host that started on it would mint links nobody could open and say nothing about it —
/// the same posture as the JWT key, for the same reason.</para>
/// </summary>
public class PublicBaseUrlSetting : IConfigurationSetting<string>
{
    public const string ConfigurationKey = "App:PublicBaseUrl";

    public const string DevelopmentDefault = "http://localhost:5180";

    public PublicBaseUrlSetting(IConfiguration configuration)
    {
        Value = Resolve(configuration);
    }

    public string Value { get; set; }

    /// <summary>The link an invitee follows. The path is ours; only the origin is configured.</summary>
    public string InviteUrl(string token) => $"{Value}/invite/{Uri.EscapeDataString(token)}";

    /// <summary>The link someone locked out follows.</summary>
    public string PasswordResetUrl(string token) => $"{Value}/reset-password/{Uri.EscapeDataString(token)}";

    private static string Resolve(IConfiguration configuration)
    {
        var configured = configuration[ConfigurationKey];

        if (!string.IsNullOrWhiteSpace(configured)) return configured.Trim().TrimEnd('/');

        var environment = configuration["ASPNETCORE_ENVIRONMENT"] ?? configuration["DOTNET_ENVIRONMENT"];

        if (string.IsNullOrWhiteSpace(environment) || string.Equals(environment, "Development", StringComparison.OrdinalIgnoreCase)) return DevelopmentDefault;

        throw new InvalidOperationException($"{ConfigurationKey} is required outside Development. It is the origin your SPA answers on — invitation and password-reset links are built from it and handed to people who must be able to open them. Set App__PublicBaseUrl=https://codespace.example.com.");
    }
}
