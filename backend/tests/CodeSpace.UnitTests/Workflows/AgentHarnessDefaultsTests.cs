using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Agents.Harnesses;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Enums;
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

/// <summary>
/// The one place the override is read from the environment, pinned end to end: the default an unauthored harness gets,
/// and the startup validation the registry the container builds runs. It SETS the process-wide variable, so it runs
/// alone (<see cref="DefaultHarnessEnvironmentCollection"/>) — nothing else reads the variable while it is set.
/// </summary>
[Trait("Category", "Unit")]
[Collection(DefaultHarnessEnvironmentCollection.Name)]
public sealed class AgentHarnessDefaultsEnvironmentTests
{
    [Fact]
    public void The_operator_override_in_the_environment_reaches_the_default_harness_and_the_registry_the_container_builds()
    {
        var original = Environment.GetEnvironmentVariable(AgentHarnessDefaults.DefaultHarnessEnvVar);

        try
        {
            Environment.SetEnvironmentVariable(AgentHarnessDefaults.DefaultHarnessEnvVar, "claude-code");

            AgentHarnessDefaults.DefaultHarness.ShouldBe("claude-code", "the override in the environment is the default an unauthored harness gets");
            Should.Throw<InvalidOperationException>(() => new AgentHarnessRegistry(new IAgentHarness[] { new FakeHarness("codex-cli") })).Message.ShouldContain("claude-code", customMessage: "the container's registry fails fast on an override no harness answers to");
            Should.NotThrow(() => new AgentHarnessRegistry(new IAgentHarness[] { new FakeHarness("codex-cli"), new FakeHarness("claude-code") }));
        }
        finally
        {
            Environment.SetEnvironmentVariable(AgentHarnessDefaults.DefaultHarnessEnvVar, original);
        }
    }

    private sealed class FakeHarness : IAgentHarness
    {
        public FakeHarness(string kind) => Kind = kind;

        public string Kind { get; }
        public string Version => "test";
        public IReadOnlyList<string> Models { get; } = new[] { "m" };

        public SandboxSpec BuildInvocation(AgentTask task) => new() { Command = "x" };
        public IReadOnlyList<AgentEvent> ParseEvents(string rawLine) => Array.Empty<AgentEvent>();
        public IAgentEventFolder CreateFolder() => new TestEventFolder((fold, exitCode) => new() { Status = AgentRunStatus.Succeeded, ExitReason = "completed" });
    }
}

/// <summary>Runs its tests with no other test running: they set the process-wide default-harness override that every harness registry and default-harness reader in the suite reads.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class DefaultHarnessEnvironmentCollection
{
    public const string Name = "DefaultHarnessEnvironment";
}
