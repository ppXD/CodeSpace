using CodeSpace.Core.Services.Agents;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Enums;
using Shouldly;

namespace CodeSpace.UnitTests.Workflows;

[Trait("Category", "Unit")]
public class AgentHarnessRegistryTests
{
    private sealed class FakeHarness : IAgentHarness
    {
        public FakeHarness(string kind) => Kind = kind;

        public string Kind { get; }
        public string Version => "test";
        public IReadOnlyList<string> Models { get; } = new[] { "m" };

        public SandboxSpec BuildInvocation(AgentTask task) => new() { Command = "x" };
        public IReadOnlyList<AgentEvent> ParseEvents(string rawLine) => Array.Empty<AgentEvent>();
        public IAgentEventFolder CreateFolder() => new TestEventFolder((fold, exitCode) =>
            new() { Status = AgentRunStatus.Succeeded, ExitReason = "completed" });
    }

    [Fact]
    public void Resolves_a_registered_harness_by_kind()
    {
        var codex = new FakeHarness("codex-cli");
        var registry = new AgentHarnessRegistry(new[] { codex });

        registry.Resolve("codex-cli").ShouldBeSameAs(codex);
    }

    [Fact]
    public void Resolve_throws_for_an_unregistered_kind()
    {
        var registry = new AgentHarnessRegistry(new[] { new FakeHarness("codex-cli") });

        Should.Throw<InvalidOperationException>(() => registry.Resolve("claude-code"));
    }

    [Fact]
    public void Construction_rejects_duplicate_kinds()
    {
        Should.Throw<InvalidOperationException>(() => new AgentHarnessRegistry(new[] { new FakeHarness("codex-cli"), new FakeHarness("codex-cli") }));
    }

    [Theory]
    [InlineData("clftaude-typo", true)]   // an override naming no registered harness fails fast at construction
    [InlineData("codex-cli", false)]      // a registered one is accepted
    [InlineData(null, false)]             // none at all is the floor
    public void Construction_validates_the_default_harness_override_it_is_handed(string? configuredDefault, bool expectThrow)
    {
        // Handed in as a value, so this pins the startup fail-fast without touching the process environment.
        var harnesses = new[] { new FakeHarness("codex-cli") };

        if (expectThrow)
            Should.Throw<InvalidOperationException>(() => new AgentHarnessRegistry(harnesses, configuredDefault)).Message.ShouldContain(configuredDefault!);
        else
            Should.NotThrow(() => new AgentHarnessRegistry(harnesses, configuredDefault));
    }

    [Fact]
    public void All_lists_every_registered_harness()
    {
        var registry = new AgentHarnessRegistry(new[] { new FakeHarness("codex-cli"), new FakeHarness("claude-code") });

        registry.All.Count.ShouldBe(2);
    }
}
