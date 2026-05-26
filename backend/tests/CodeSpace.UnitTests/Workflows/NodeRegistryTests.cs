using CodeSpace.Core.Services.Workflows.Nodes;
using CodeSpace.Messages.Enums;
using Shouldly;

namespace CodeSpace.UnitTests.Workflows;

public class NodeRegistryTests
{
    [Fact]
    public void Resolves_by_typekey()
    {
        var registry = new NodeRegistry(new INodeRuntime[]
        {
            new StubNode("trigger.x"),
            new StubNode("git.fetch")
        });

        registry.Resolve("git.fetch").TypeKey.ShouldBe("git.fetch");
        registry.Contains("git.fetch").ShouldBeTrue();
        registry.Contains("missing").ShouldBeFalse();
    }

    [Fact]
    public void Unknown_typekey_throws()
    {
        var registry = new NodeRegistry(new INodeRuntime[] { new StubNode("trigger.x") });

        Should.Throw<InvalidOperationException>(() => registry.Resolve("missing"));
    }

    [Fact]
    public void Duplicate_typekey_fails_construction()
    {
        var nodes = new INodeRuntime[] { new StubNode("dup"), new StubNode("dup") };

        Should.Throw<InvalidOperationException>(() => new NodeRegistry(nodes));
    }

    [Fact]
    public void All_exposes_every_loaded_node()
    {
        var registry = new NodeRegistry(new INodeRuntime[]
        {
            new StubNode("a"),
            new StubNode("b"),
            new StubNode("c")
        });

        registry.All.Count.ShouldBe(3);
    }

    private sealed class StubNode : INodeRuntime
    {
        public StubNode(string typeKey) { TypeKey = typeKey; }
        public string TypeKey { get; }
        public NodeManifest Manifest { get; } = new()
        {
            DisplayName = "stub",
            Category = "test",
            Kind = NodeKind.Regular,
            ConfigSchema = SchemaBuilder.EmptyObject(),
            InputSchema = SchemaBuilder.EmptyObject(),
            OutputSchema = SchemaBuilder.EmptyObject()
        };

        public Task<NodeResult> RunAsync(NodeRunContext context, CancellationToken cancellationToken) => Task.FromResult(NodeResult.Ok());
    }
}
