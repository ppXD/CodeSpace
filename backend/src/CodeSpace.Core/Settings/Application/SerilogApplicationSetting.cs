using Microsoft.Extensions.Configuration;

namespace CodeSpace.Core.Settings.Application;

public class SerilogApplicationSetting : IConfigurationSetting<string>
{
    public SerilogApplicationSetting(IConfiguration configuration)
    {
        Value = configuration.GetValue<string>("Serilog:Application") ?? "CodeSpace";
    }

    public string Value { get; set; }
}
