using CodeSpace.Core.Services.OutboundHttp;
using Microsoft.Extensions.Configuration;

namespace CodeSpace.Core.Settings.OutboundHttp;

/// <summary>
/// The operator's committed allowlist of INTERNAL destinations workflow-driven outbound HTTP (the <c>http.request</c>
/// node) may reach — host names, addresses and CIDR ranges that the destination guard would otherwise refuse as
/// loopback, private, link-local or otherwise non-public. Read once, while the host is being built
/// (<c>GuardedHttpClientRegistration.AddGuardedHttpClient</c>), so a malformed entry stops the host from starting. It is
/// a list in appsettings, changed by committing a value, not a switch: the guard itself cannot be turned off.
///
/// <para>The key's literal value is pinned by a unit test: renaming it would silently empty every deployment's
/// allowlist that used the old name, and their intentional internal targets would start failing.</para>
/// </summary>
public class OutboundHttpAllowlistSetting : IConfigurationSetting<OutboundDestinationAllowlist>
{
    public const string ConfigurationKey = "OutboundHttp:AllowedInternalDestinations";

    public OutboundHttpAllowlistSetting(IConfiguration configuration)
    {
        Value = OutboundDestinationAllowlist.Parse(configuration.GetSection(ConfigurationKey).Get<string[]>() ?? Array.Empty<string>());
    }

    public OutboundDestinationAllowlist Value { get; set; }
}
