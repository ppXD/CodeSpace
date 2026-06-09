using CodeSpace.Core.Settings;
using Shouldly;

namespace CodeSpace.UnitTests.Settings;

[Trait("Category", "Unit")]
public class ShutdownSettingsTests
{
    [Fact]
    public void Env_var_name_and_default_are_pinned()
    {
        // Renaming the env var breaks an operator who aligned it with their k8s grace period (Rule 8).
        ShutdownSettings.DrainSecondsEnvVar.ShouldBe("CODESPACE_SHUTDOWN_DRAIN_SECONDS");
        ShutdownSettings.DefaultDrainSeconds.ShouldBe(30);
    }

    [Fact]
    public void Resolves_the_default_when_env_is_unset()
    {
        WithEnv(null, () => ShutdownSettings.ResolveDrainTimeout().ShouldBe(TimeSpan.FromSeconds(30)));
    }

    [Fact]
    public void Resolves_the_env_override_when_a_positive_integer()
    {
        WithEnv("90", () => ShutdownSettings.ResolveDrainTimeout().ShouldBe(TimeSpan.FromSeconds(90)));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-5")]
    [InlineData("not-a-number")]
    [InlineData("")]
    public void Falls_back_to_default_for_an_invalid_override(string raw)
    {
        WithEnv(raw, () => ShutdownSettings.ResolveDrainTimeout().ShouldBe(TimeSpan.FromSeconds(30)));
    }

    private static void WithEnv(string? value, Action assert)
    {
        var original = System.Environment.GetEnvironmentVariable(ShutdownSettings.DrainSecondsEnvVar);
        try
        {
            System.Environment.SetEnvironmentVariable(ShutdownSettings.DrainSecondsEnvVar, value);
            assert();
        }
        finally
        {
            System.Environment.SetEnvironmentVariable(ShutdownSettings.DrainSecondsEnvVar, original);
        }
    }
}
