using CodeSpace.Core.Settings;
using Microsoft.Extensions.Configuration;

namespace CodeSpace.IntegrationTests.Settings;

public class TestPostgresAdminConnectionString : IConfigurationSetting<string>
{
    public TestPostgresAdminConnectionString(IConfiguration configuration)
    {
        Value = configuration.GetValue<string>("TestPostgres:AdminConnectionString")
            ?? throw new InvalidOperationException("TestPostgres:AdminConnectionString must be configured in appsettings.json or appsettings.Local.json");
    }

    public string Value { get; set; }
}
