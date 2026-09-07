using System.Text;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Settings;
using CodeSpace.Messages.Agents;
using Microsoft.Extensions.Configuration;
using Shouldly;

namespace CodeSpace.UnitTests.Settings;

[Trait("Category", "Unit")]
public class SandboxConfigurationTests
{
    [Fact]
    public void Security_configuration_keys_are_pinned()
    {
        RuntimeSettings.MaxAutonomyKey.ShouldBe("Sandbox:MaxAutonomy");
        RuntimeSettings.RequireConfinementKey.ShouldBe("Sandbox:RequireConfinement");
        RuntimeSettings.AgentMemoryCeilingMbKey.ShouldBe("Sandbox:AgentMemoryCeilingMb");
    }

    [Theory]
    [InlineData("Confined", AgentAutonomyLevel.Confined)]
    [InlineData(" standard ", AgentAutonomyLevel.Standard)]
    [InlineData("TRUSTED", AgentAutonomyLevel.Trusted)]
    [InlineData("Unleashed", AgentAutonomyLevel.Unleashed)]
    public void Valid_autonomy_names_preserve_the_configured_deployment_ceiling(string value, AgentAutonomyLevel expected)
    {
        using var scope = RuntimeSettings.Override(Read(RuntimeSettings.MaxAutonomyKey, value));

        AgentAutonomyPolicy.DeploymentCeiling.ShouldBe(expected);
    }

    [Fact]
    public void Absent_sandbox_settings_preserve_the_existing_deployment_defaults()
    {
        var settings = RuntimeSettings.Read(new ConfigurationBuilder().Build());
        using var scope = RuntimeSettings.Override(settings);

        settings.RequireSandboxConfinement.ShouldBeFalse();
        settings.AgentMemoryCeilingMb.ShouldBeNull();
        settings.AgentCgroupRoot.ShouldBeNull();
        settings.MaxAutonomy.ShouldBeNull();
        AgentAutonomyPolicy.DeploymentCeiling.ShouldBe(AgentAutonomyLevel.Unleashed);
    }

    [Theory]
    [InlineData("Sandbox:MaxAutonomy", "Unlesahed")]
    [InlineData("Sandbox:MaxAutonomy", "")]
    [InlineData("Sandbox:MaxAutonomy", "   ")]
    [InlineData("Sandbox:MaxAutonomy", "0")]
    [InlineData("Sandbox:MaxAutonomy", "3")]
    [InlineData("Sandbox:MaxAutonomy", "999")]
    [InlineData("Sandbox:MaxAutonomy", "Standard, Trusted")]
    [InlineData("Sandbox:RequireConfinement", "flase")]
    [InlineData("Sandbox:RequireConfinement", "")]
    [InlineData("Sandbox:RequireConfinement", "1")]
    [InlineData("Sandbox:AgentMemoryCeilingMb", "")]
    [InlineData("Sandbox:AgentMemoryCeilingMb", "  ")]
    [InlineData("Sandbox:AgentMemoryCeilingMb", "small")]
    [InlineData("Sandbox:AgentMemoryCeilingMb", "0")]
    [InlineData("Sandbox:AgentMemoryCeilingMb", "-1")]
    [InlineData("Sandbox:AgentMemoryCeilingMb", "1.5")]
    [InlineData("Sandbox:AgentMemoryCeilingMb", "2147483648")]
    public void Malformed_security_values_are_rejected_before_they_can_restore_broader_defaults(string key, string value)
    {
        var exception = Should.Throw<SandboxConfigurationException>(() => Read(key, value));

        exception.ConfigurationKey.ShouldBe(key);
        exception.Message.ShouldContain(key);
    }

    [Theory]
    [InlineData("true", true)]
    [InlineData("FALSE", false)]
    [InlineData(" True ", true)]
    public void Explicit_confinement_values_are_preserved(string value, bool expected)
    {
        Read("Sandbox:RequireConfinement", value).RequireSandboxConfinement.ShouldBe(expected);
    }

    [Theory]
    [InlineData("1", 1)]
    [InlineData(" 1536 ", 1536)]
    [InlineData("2147483647", int.MaxValue)]
    public void Positive_memory_limits_are_preserved(string value, int expected)
    {
        Read("Sandbox:AgentMemoryCeilingMb", value).AgentMemoryCeilingMb.ShouldBe(expected);
    }

    [Theory]
    [InlineData("Sandbox:MaxAutonomy", "Trusted", "Standrad")]
    [InlineData("Sandbox:RequireConfinement", "true", "flase")]
    [InlineData("Sandbox:AgentMemoryCeilingMb", "1536", "small")]
    public void Only_the_effective_configuration_provider_value_is_validated(string key, string valid, string invalid)
    {
        var corrected = new ConfigurationBuilder().AddInMemoryCollection(Values(key, invalid)).AddInMemoryCollection(Values(key, valid)).Build();
        var brokenOverride = new ConfigurationBuilder().AddInMemoryCollection(Values(key, valid)).AddInMemoryCollection(Values(key, invalid)).Build();

        Should.NotThrow(() => RuntimeSettings.Read(corrected));
        Should.Throw<InvalidOperationException>(() => RuntimeSettings.Read(brokenOverride)).Message.ShouldContain(key);
    }

    [Fact]
    public void Environment_provider_overrides_json_through_the_normal_section_key_mapping()
    {
        var prefix = $"CODESPACE_SANDBOX_TEST_{Guid.NewGuid():N}_";
        var environmentKey = prefix + "Sandbox__MaxAutonomy";
        try
        {
            Environment.SetEnvironmentVariable(environmentKey, "Confined");
            using var json = new MemoryStream(Encoding.UTF8.GetBytes("{\"Sandbox\":{\"MaxAutonomy\":\"Standrad\"}}"));
            var configuration = new ConfigurationBuilder().AddJsonStream(json).AddEnvironmentVariables(prefix).Build();
            using var scope = RuntimeSettings.Override(RuntimeSettings.Read(configuration));

            AgentAutonomyPolicy.DeploymentCeiling.ShouldBe(AgentAutonomyLevel.Confined);
        }
        finally
        {
            Environment.SetEnvironmentVariable(environmentKey, null);
        }
    }

    [Theory]
    [InlineData("Standrad")]
    [InlineData("999")]
    [InlineData("")]
    public void Direct_deployment_settings_cannot_bypass_the_strict_ceiling_parser(string value)
    {
        using var scope = RuntimeSettings.Override(new RuntimeSettings { MaxAutonomy = value });

        Should.Throw<InvalidOperationException>(() => AgentAutonomyPolicy.DeploymentCeiling).Message.ShouldContain(RuntimeSettings.MaxAutonomyKey);
    }

    private static RuntimeSettings Read(string key, string? value) => RuntimeSettings.Read(new ConfigurationBuilder().AddInMemoryCollection(Values(key, value)).Build());

    private static Dictionary<string, string?> Values(string key, string? value) => new() { [key] = value };
}
