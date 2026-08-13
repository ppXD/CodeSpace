using Microsoft.Extensions.Configuration;

namespace CodeSpace.Core.Settings.Logging;

/// <summary>
/// The Seq API key this process authenticates its log ingestion with, when the server asks for one.
///
/// <para>Blank is the normal case and not a misconfiguration: a Seq that accepts anonymous ingestion — every local
/// one, by default — needs no key. So this is deliberately NOT required alongside
/// <see cref="SerilogServerUrlSetting"/>; a deployment whose Seq is locked down sets it, everyone else leaves it
/// empty and the sink posts without the header.</para>
/// </summary>
public class SerilogApiKeySetting : IConfigurationSetting<string?>
{
    public const string ConfigurationKey = "Serilog:Seq:ApiKey";

    public SerilogApiKeySetting(IConfiguration configuration)
    {
        Value = configuration.GetValue<string?>(ConfigurationKey);
    }

    public string? Value { get; set; }
}
