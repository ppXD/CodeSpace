using CodeSpace.Core.Settings;
using Microsoft.Extensions.Configuration;

namespace CodeSpace.IntegrationTests.Settings;

public class TestDatabaseNamePrefix : IConfigurationSetting<string>
{
    public TestDatabaseNamePrefix(IConfiguration configuration)
    {
        Value = configuration.GetValue<string>("TestPostgres:TestDatabaseNamePrefix") ?? "codespace_test_";
    }

    public string Value { get; set; }
}
