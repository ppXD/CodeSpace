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
        public AgentEvent? ParseEvent(string rawLine) => null;
        public AgentRunResult BuildResult(IReadOnlyList<AgentEvent> events, int exitCode) =>
            new() { Status = AgentRunStatus.Succeeded, ExitReason = "completed" };
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

    [Fact]
    public void All_lists_every_registered_harness()
    {
        var registry = new AgentHarnessRegistry(new[] { new FakeHarness("codex-cli"), new FakeHarness("claude-code") });

        registry.All.Count.ShouldBe(2);
    }
}
