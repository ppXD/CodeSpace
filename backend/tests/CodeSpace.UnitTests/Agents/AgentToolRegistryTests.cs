using System.Text.Json;
using CodeSpace.Core.Services.Agents.Commands;
using CodeSpace.Core.Services.Agents.Tools;
using CodeSpace.Core.Services.Workflows.Nodes;
using CodeSpace.Core.Services.Workflows.Nodes.Builtin;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Enums;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace CodeSpace.UnitTests.Agents;

/// <summary>
/// Pins the agent-tool catalog: it projects ONLY tool-eligible nodes onto the fabric, resolves by kind, fails
/// loudly on a duplicate kind, and the real agent.run_command node is actually marked eligible.
/// </summary>
[Trait("Category", "Unit")]
public class AgentToolRegistryTests
{
    private sealed class FakeNode : INodeRuntime
    {
        public FakeNode(string typeKey, bool eligible)
        {
            TypeKey = typeKey;
            Manifest = new NodeManifest
            {
                DisplayName = typeKey, Category = "Test", Kind = NodeKind.Regular,
                IsAgentToolEligible = eligible,
                ConfigSchema = SchemaBuilder.EmptyObject(), InputSchema = SchemaBuilder.EmptyObject(), OutputSchema = SchemaBuilder.EmptyObject(),
            };
        }
        public string TypeKey { get; }
        public NodeManifest Manifest { get; }
        public Task<NodeResult> RunAsync(NodeRunContext context, CancellationToken ct) => Task.FromResult(NodeResult.Ok());
    }

    private sealed class StubRunCommandService : IRunCommandService
    {
        public Task<SandboxResult> RunAsync(RunCommandRequest request, CancellationToken ct) =>
            Task.FromResult(new SandboxResult { Status = SandboxStatus.Success, ExitCode = 0, Stdout = "", Stderr = "" });
    }

    private static AgentToolRegistry Build(params INodeRuntime[] nodes) => new(nodes, NullLoggerFactory.Instance);

    [Fact]
    public void Only_eligible_nodes_are_projected_as_tools()
    {
        var registry = Build(
            new FakeNode("git.read", eligible: true),
            new FakeNode("agent.code", eligible: false),    // suspends → not a tool
            new FakeNode("trigger.push", eligible: false),  // trigger → not a tool
            new FakeNode("run.cmd", eligible: true));

        registry.All.Select(t => t.Kind).ShouldBe(new[] { "git.read", "run.cmd" }, "sorted, eligible-only");
    }

    [Fact]
    public void Resolve_returns_the_tool_by_kind_or_null()
    {
        var registry = Build(new FakeNode("git.read", eligible: true));

        registry.Resolve("git.read").ShouldNotBeNull();
        registry.Resolve("git.read")!.Kind.ShouldBe("git.read");
        registry.Resolve("nope").ShouldBeNull();
        registry.Resolve("agent.code").ShouldBeNull("an ineligible node is not resolvable");
    }

    [Fact]
    public void Duplicate_tool_kinds_fail_loudly()
    {
        Should.Throw<InvalidOperationException>(() => Build(new FakeNode("dup", true), new FakeNode("dup", true)))
            .Message.ShouldContain("dup");
    }

    [Fact]
    public void An_empty_node_set_yields_an_empty_catalog()
    {
        Build().All.ShouldBeEmpty();
    }

    [Fact]
    public void The_real_run_command_node_is_marked_tool_eligible_and_appears_in_the_catalog()
    {
        var node = new AgentRunCommandNode(new StubRunCommandService());
        node.Manifest.IsAgentToolEligible.ShouldBeTrue("agent.run_command is a synchronous, standalone tool");

        Build(node).Resolve("agent.run_command").ShouldNotBeNull();
    }
}
