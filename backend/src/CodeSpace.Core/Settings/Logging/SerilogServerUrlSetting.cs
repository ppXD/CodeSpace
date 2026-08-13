using Microsoft.Extensions.Configuration;

namespace CodeSpace.Core.Settings.Logging;

/// <summary>
/// The Seq server this process ships its structured log events to — the searchable copy of everything the console
/// prints once and then loses when the pod is replaced.
///
/// <para>The shipped default is a Seq on the developer's own machine, and nothing verifies it is there. The sink
/// posts in the background, so a developer with no Seq running sees exactly what they saw before — the console —
/// and never pays for the absent server at boot. Startup must not depend on a logging destination being up.</para>
///
/// <para>A blank value turns Seq off outright. That is the honest way for a deployment to say "console only":
/// pointing the sink at a host nobody runs would otherwise leave it retrying batches forever against nothing.</para>
/// </summary>
public class SerilogServerUrlSetting : IConfigurationSetting<string?>
{
    public const string ConfigurationKey = "Serilog:Seq:ServerUrl";

    public SerilogServerUrlSetting(IConfiguration configuration)
    {
        Value = configuration.GetValue<string?>(ConfigurationKey);
    }

    public string? Value { get; set; }
}
