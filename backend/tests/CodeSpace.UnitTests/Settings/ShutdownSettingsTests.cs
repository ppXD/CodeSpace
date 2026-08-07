using CodeSpace.Core.Settings;
using Microsoft.Extensions.Configuration;
using Shouldly;

namespace CodeSpace.UnitTests.Settings;

[Trait("Category", "Unit")]
public class ShutdownSettingsTests
{
    [Fact]
    public void The_default_is_pinned_to_the_orchestrator_default()
    {
        // A deployment's terminationGracePeriodSeconds has to be at least this, so the number is part of the
        // deployment contract; 30 matches k8s's own default, which is why an unconfigured pod drains cleanly.
        RuntimeSettings.DefaultShutdownDrainSeconds.ShouldBe(30);
    }

    [Fact]
    public void Resolves_the_default_when_unconfigured()
    {
        WithDrainSeconds(null, () => ShutdownSettings.ResolveDrainTimeout().ShouldBe(TimeSpan.FromSeconds(30)));
    }

    [Fact]
    public void Resolves_the_configured_value_when_a_positive_integer()
    {
        WithDrainSeconds("90", () => ShutdownSettings.ResolveDrainTimeout().ShouldBe(TimeSpan.FromSeconds(90)));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-5")]
    [InlineData("not-a-number")]
    [InlineData("")]
    public void Falls_back_to_the_default_for_an_unusable_value(string raw)
    {
        // Zero or negative would mean "kill in-flight work immediately", which nobody configures on purpose, and an
        // unparseable value is a typo — both land on the default rather than on a surprising drain budget.
        WithDrainSeconds(raw, () => ShutdownSettings.ResolveDrainTimeout().ShouldBe(TimeSpan.FromSeconds(30)));
    }

    private static void WithDrainSeconds(string? value, Action assert)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Shutdown:DrainSeconds"] = value })
            .Build();

        using (RuntimeSettings.Override(RuntimeSettings.Read(configuration))) assert();
    }
}
