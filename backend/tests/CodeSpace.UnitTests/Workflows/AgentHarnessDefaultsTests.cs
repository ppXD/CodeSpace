using CodeSpace.Core.Services.Agents.Harnesses;
using Shouldly;

namespace CodeSpace.UnitTests.Workflows;

/// <summary>
/// Pins the platform DEFAULT harness — the single source of truth (<see cref="AgentHarnessDefaults"/>, Rule 8) used by
/// every projection + the supervisor spawn when neither the operator nor the model authored a harness. UNSET → the
/// codex-cli floor (byte-identical to the prior hardcoded default the projection / spawn tests still pin); SET → the
/// operator's override (trimmed), so an air-gapped / fork operator can flip the global default off codex in ONE place.
///
/// This test MUTATES the process-global override env var, so it shares the "DefaultHarnessEnvMutation" collection with
/// every test that READS the unset default (the definition builders / planner / supervisor build) — same collection =
/// run sequentially, so a concurrent reader can never observe a transient override (mirrors the repo's McpEndpointEnvMutation pattern).
/// </summary>
[Trait("Category", "Unit")]
[Collection("DefaultHarnessEnvMutation")]
public sealed class AgentHarnessDefaultsTests
{
    [Fact]
    public void DefaultHarnessEnvVar_name_is_pinned() =>
        // Renaming this silently ignores the override for any operator who pinned a non-codex default via env (Rule 8).
        AgentHarnessDefaults.DefaultHarnessEnvVar.ShouldBe("CODESPACE_DEFAULT_HARNESS");

    [Theory]
    [InlineData(null, "codex-cli")]            // unset → the safe floor (byte-identical to the prior hardcoded default)
    [InlineData("", "codex-cli")]              // blank → the floor
    [InlineData("   ", "codex-cli")]           // whitespace → the floor
    [InlineData("claude-code", "claude-code")] // a set override flips the global default off codex
    [InlineData("  claude-code  ", "claude-code")]   // trimmed
    public void DefaultHarness_is_the_env_override_else_the_codex_floor(string? envValue, string expected)
    {
        var original = Environment.GetEnvironmentVariable(AgentHarnessDefaults.DefaultHarnessEnvVar);
        try
        {
            Environment.SetEnvironmentVariable(AgentHarnessDefaults.DefaultHarnessEnvVar, envValue);

            AgentHarnessDefaults.DefaultHarness.ShouldBe(expected);
        }
        finally
        {
            Environment.SetEnvironmentVariable(AgentHarnessDefaults.DefaultHarnessEnvVar, original);
        }
    }

    [Theory]
    [InlineData(null, false)]            // unset → no-op
    [InlineData("", false)]              // blank → no-op
    [InlineData("codex-cli", false)]     // registered (case-exact) → no-op
    [InlineData("CLAUDE-CODE", false)]   // registered (case-insensitive) → no-op
    [InlineData("clftaude-typo", true)]  // a typo'd / unregistered kind → fail-fast throw
    public void Validate_fails_fast_only_for_an_unregistered_override(string? envValue, bool expectThrow)
    {
        var registered = new[] { "codex-cli", "claude-code" };
        var original = Environment.GetEnvironmentVariable(AgentHarnessDefaults.DefaultHarnessEnvVar);
        try
        {
            Environment.SetEnvironmentVariable(AgentHarnessDefaults.DefaultHarnessEnvVar, envValue);

            if (expectThrow)
            {
                // The error must name the bad kind so the operator can fix the typo.
                var ex = Should.Throw<InvalidOperationException>(() => AgentHarnessDefaults.Validate(registered));
                ex.Message.ShouldContain(envValue!.Trim());
            }
            else
                Should.NotThrow(() => AgentHarnessDefaults.Validate(registered));
        }
        finally
        {
            Environment.SetEnvironmentVariable(AgentHarnessDefaults.DefaultHarnessEnvVar, original);
        }
    }
}

/// <summary>Groups the env-mutating <see cref="AgentHarnessDefaultsTests"/> with every test that reads the unset default-harness, so they run SEQUENTIALLY (a collection is xUnit's parallelization boundary) — the mutator never overlaps a reader. Plain (not DisableParallelization), so the group still runs in parallel with the rest of the suite.</summary>
[CollectionDefinition("DefaultHarnessEnvMutation")]
public sealed class DefaultHarnessEnvMutationCollection { }
