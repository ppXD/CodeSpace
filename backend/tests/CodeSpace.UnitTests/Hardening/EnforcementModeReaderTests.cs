using CodeSpace.Core.Hardening;
using Shouldly;

namespace CodeSpace.UnitTests.Hardening;

/// <summary>
/// Unit pins for the env-var → <see cref="EnforcementMode"/> parser. Every hardening
/// validator across the codebase routes through this so a regression here silently
/// breaks every operator override; treat the alias table as a hard contract.
/// </summary>
[Trait("Category", "Unit")]
public class EnforcementModeReaderTests : IDisposable
{
    // Unique env-var name per test class so concurrent xunit runs can't cross-contaminate.
    private const string TestEnvVar = "CODESPACE_TEST_ENFORCEMENT_MODE_READER";

    public void Dispose() => Environment.SetEnvironmentVariable(TestEnvVar, null);

    [Theory]
    [InlineData("off"      , EnforcementMode.Off)]
    [InlineData("OFF"      , EnforcementMode.Off)]
    [InlineData("disabled" , EnforcementMode.Off)]
    [InlineData("0"        , EnforcementMode.Off)]
    [InlineData("false"    , EnforcementMode.Off)]
    [InlineData("warn"     , EnforcementMode.Warn)]
    [InlineData("Warning"  , EnforcementMode.Warn)]
    [InlineData("strict"   , EnforcementMode.Strict)]
    [InlineData("enforce"  , EnforcementMode.Strict)]
    [InlineData("1"        , EnforcementMode.Strict)]
    [InlineData("TRUE"     , EnforcementMode.Strict)]
    [InlineData("  strict ", EnforcementMode.Strict)]
    public void Parses_alias_into_expected_mode(string raw, EnforcementMode expected)
    {
        Environment.SetEnvironmentVariable(TestEnvVar, raw);
        EnforcementModeReader.Read(TestEnvVar).ShouldBe(expected);
    }

    [Theory]
    [InlineData(null)]     // env var unset
    [InlineData("")]        // empty
    [InlineData("   ")]     // whitespace
    public void Returns_default_when_env_var_missing_or_empty(string? raw)
    {
        Environment.SetEnvironmentVariable(TestEnvVar, raw);
        // Default of the reader itself is Warn (CLAUDE.md Rule 11 v0 rollout).
        EnforcementModeReader.Read(TestEnvVar).ShouldBe(EnforcementMode.Warn);
    }

    [Theory]
    [InlineData("typo")]
    [InlineData("loud")]
    [InlineData("2")]
    public void Unrecognised_value_falls_back_to_default_never_throws(string raw)
    {
        Environment.SetEnvironmentVariable(TestEnvVar, raw);
        // A typo at the operator's terminal must not crash the engine — fall back to
        // the documented default. The validator's warn-mode log line then surfaces the
        // alias-mismatch in operator logs the next time the check fires.
        EnforcementModeReader.Read(TestEnvVar).ShouldBe(EnforcementMode.Warn);
        EnforcementModeReader.Read(TestEnvVar, EnforcementMode.Off).ShouldBe(EnforcementMode.Off);
    }

    [Fact]
    public void Caller_supplied_default_wins_over_reader_default()
    {
        Environment.SetEnvironmentVariable(TestEnvVar, null);
        EnforcementModeReader.Read(TestEnvVar, EnforcementMode.Strict).ShouldBe(EnforcementMode.Strict);
        EnforcementModeReader.Read(TestEnvVar, EnforcementMode.Off).ShouldBe(EnforcementMode.Off);
    }
}
