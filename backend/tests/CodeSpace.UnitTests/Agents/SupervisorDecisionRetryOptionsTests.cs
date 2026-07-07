using CodeSpace.Core.Services.Supervisor.Deciders;
using Shouldly;

namespace CodeSpace.UnitTests.Agents;

/// <summary>
/// 🟢 Unit: <see cref="SupervisorDecisionRetryOptions"/> env-override reading. Pins the env-var NAMES (renaming one
/// breaks every operator who pinned a slow-gateway override) and the safe clamping (a fat-fingered value can never
/// disable the bound or pin a worker indefinitely). Per CLAUDE.md Rule 8.
/// </summary>
[Trait("Category", "Unit")]
public class SupervisorDecisionRetryOptionsTests
{
    [Fact]
    public void Env_var_names_are_pinned()
    {
        SupervisorDecisionRetryOptions.MaxAttemptsEnvVar.ShouldBe("CODESPACE_SUPERVISOR_DECISION_MAX_ATTEMPTS");
        SupervisorDecisionRetryOptions.TimeoutSecondsEnvVar.ShouldBe("CODESPACE_SUPERVISOR_DECISION_TIMEOUT_SECONDS");
    }

    [Fact]
    public void Defaults_apply_when_unset()
    {
        using var _ = new EnvScope(SupervisorDecisionRetryOptions.MaxAttemptsEnvVar, null);
        using var __ = new EnvScope(SupervisorDecisionRetryOptions.TimeoutSecondsEnvVar, null);

        var options = SupervisorDecisionRetryOptions.FromEnvironment();

        options.MaxAttempts.ShouldBe(5);
        options.PerCallTimeout.ShouldBe(TimeSpan.FromSeconds(600));
    }

    [Fact]
    public void Backoff_ceilings_are_pinned()
    {
        // Raising RetryAfterCeiling re-opens the "hostile Retry-After pins a worker for hours" hole; raising
        // BackoffCeiling unbounds the in-process sleep between attempts. Hard-pin both (Rule 8).
        SupervisorDecisionRetryOptions.RetryAfterCeiling.ShouldBe(TimeSpan.FromMinutes(15));
        SupervisorDecisionRetryOptions.BackoffCeiling.ShouldBe(TimeSpan.FromSeconds(60));
    }

    [Theory]
    [InlineData("4", 4)]          // in range → honored
    [InlineData("99", 10)]        // above max → clamped to 10
    [InlineData("0", 1)]          // below min → clamped to 1
    [InlineData("not-a-number", 5)] // garbage → default
    public void MaxAttempts_is_read_and_clamped(string raw, int expected)
    {
        using var _ = new EnvScope(SupervisorDecisionRetryOptions.MaxAttemptsEnvVar, raw);

        SupervisorDecisionRetryOptions.FromEnvironment().MaxAttempts.ShouldBe(expected);
    }

    [Theory]
    [InlineData("120", 120)]   // in range → honored
    [InlineData("5", 10)]      // below min 10 → clamped
    [InlineData("9999", 900)]  // above max 900 → clamped
    [InlineData("", 600)]      // blank → default
    public void Timeout_seconds_is_read_and_clamped(string raw, int expectedSeconds)
    {
        using var _ = new EnvScope(SupervisorDecisionRetryOptions.TimeoutSecondsEnvVar, raw);

        SupervisorDecisionRetryOptions.FromEnvironment().PerCallTimeout.ShouldBe(TimeSpan.FromSeconds(expectedSeconds));
    }

    /// <summary>Sets an env var for the scope of one test and restores the prior value on dispose (tests in one class run serially, so no cross-test race).</summary>
    private sealed class EnvScope : IDisposable
    {
        private readonly string _name;
        private readonly string? _prior;

        public EnvScope(string name, string? value)
        {
            _name = name;
            _prior = Environment.GetEnvironmentVariable(name);
            Environment.SetEnvironmentVariable(name, value);
        }

        public void Dispose() => Environment.SetEnvironmentVariable(_name, _prior);
    }
}
