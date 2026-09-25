using CodeSpace.Core.Services.Agents.Harnesses;
using Shouldly;

namespace CodeSpace.UnitTests.Workflows;

/// <summary>
/// Pins the platform DEFAULT harness — the single source of truth (<see cref="AgentHarnessDefaults"/>, Rule 8) used by
/// every projection + the supervisor spawn when neither the operator nor the model authored a harness. UNSET → the
/// codex-cli floor (byte-identical to the prior hardcoded default the projection / spawn tests still pin); SET → the
/// operator's override (trimmed), so an air-gapped / fork operator can flip the global default off codex in ONE place.
///
/// <para>The override is read from the process environment in production and handed in as a value here, so nothing in
/// this class touches that environment — xunit runs test classes in parallel in one process, and a value set here was
/// read by every class constructing a harness registry beside it.</para>
/// </summary>
[Trait("Category", "Unit")]
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
    public void DefaultHarness_is_the_configured_override_else_the_codex_floor(string? configured, string expected) =>
        AgentHarnessDefaults.DefaultHarnessFrom(configured).ShouldBe(expected);

    [Theory]
    [InlineData(null, false)]            // unset → no-op
    [InlineData("", false)]              // blank → no-op
    [InlineData("codex-cli", false)]     // registered (case-exact) → no-op
    [InlineData("CLAUDE-CODE", false)]   // registered (case-insensitive) → no-op
    [InlineData("clftaude-typo", true)]  // a typo'd / unregistered kind → fail-fast throw
    public void Validate_fails_fast_only_for_an_unregistered_override(string? configured, bool expectThrow)
    {
        var registered = new[] { "codex-cli", "claude-code" };

        if (expectThrow)
            // The error must name the bad kind so the operator can fix the typo.
            Should.Throw<InvalidOperationException>(() => AgentHarnessDefaults.Validate(registered, configured)).Message.ShouldContain(configured!.Trim());
        else
            Should.NotThrow(() => AgentHarnessDefaults.Validate(registered, configured));
    }
}
