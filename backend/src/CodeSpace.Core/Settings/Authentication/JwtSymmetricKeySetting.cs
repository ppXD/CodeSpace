using Microsoft.Extensions.Configuration;

namespace CodeSpace.Core.Settings.Authentication;

public class JwtSymmetricKeySetting : IConfigurationSetting<string>
{
    public JwtSymmetricKeySetting(IConfiguration configuration)
    {
        Value = configuration.GetValue<string>("Authentication:Jwt:SymmetricKey") ?? string.Empty;
    }

    public string Value { get; set; }
}
