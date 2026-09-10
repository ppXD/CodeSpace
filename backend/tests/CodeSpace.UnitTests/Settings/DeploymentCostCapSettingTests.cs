using System.Collections.Generic;
using CodeSpace.Core.Settings;
using Microsoft.Extensions.Configuration;
using Shouldly;

namespace CodeSpace.UnitTests.Settings;

/// <summary>
/// Pins the deployment cost-cap fallback: its key, its absent-by-default value, and that a malformed value FAILS
/// STARTUP instead of falling back.
///
/// <para>The default matters more than it looks. An unset value must leave a deployment exactly as it was before
/// P15-5b-ii — per-run ceilings only — because a fallback that appeared with the release would refuse live runs on
/// a limit nobody configured. And the failure direction matters: for a ceiling, "fall back to the default" means
/// "no ceiling", so a typo landing on the default is a typo that lifts the limit.</para>
/// </summary>
[Trait("Category", "Unit")]
public class DeploymentCostCapSettingTests
{
    [Fact]
    public void The_configuration_key_is_pinned() => RuntimeSettings.DeploymentCostCapUsdKey.ShouldBe("Budget:DeploymentCostCapUsd");

    [Fact]
    public void An_absent_setting_leaves_the_deployment_uncapped()
    {
        var settings = RuntimeSettings.Read(new ConfigurationBuilder().Build());

        settings.DeploymentCostCapUsd.ShouldBeNull("an unset fallback must not start refusing runs on a limit nobody configured");
    }

    [Fact]
    public void A_default_constructed_settings_record_is_uncapped_too() => new RuntimeSettings().DeploymentCostCapUsd.ShouldBeNull();

    [Theory]
    [InlineData("50", 50)]
    [InlineData("50.25", 50.25)]
    [InlineData(" 0.0001 ", 0.0001)]
    public void A_positive_amount_is_read(string configured, decimal expected) =>
        Read(configured).DeploymentCostCapUsd.ShouldBe(expected);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void A_blank_value_means_unset_rather_than_zero(string? configured) => Read(configured).DeploymentCostCapUsd.ShouldBeNull();

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("fifty")]
    [InlineData("50usd")]
    public void A_malformed_or_non_positive_amount_fails_startup(string configured) =>
        Should.Throw<InvalidOperationException>(() => Read(configured))
            .Message.ShouldContain(RuntimeSettings.DeploymentCostCapUsdKey, customMessage: "the refusal must name the key an operator has to fix");

    private static RuntimeSettings Read(string? value) =>
        RuntimeSettings.Read(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { [RuntimeSettings.DeploymentCostCapUsdKey] = value }).Build());
}
