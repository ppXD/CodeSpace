using Microsoft.Extensions.Configuration;

namespace CodeSpace.Core.Settings.Database;

public class CodeSpaceConnectionString : IConfigurationSetting<string>
{
    public CodeSpaceConnectionString(IConfiguration configuration)
    {
        Value = configuration.GetValue<string>("CodeSpaceStore:ConnectionString") ?? string.Empty;
    }

    public string Value { get; set; }
}
