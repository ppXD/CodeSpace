using CodeSpace.Core.Services.Tasks.Projection;
using CodeSpace.Messages.Dtos.Workflows;
using CodeSpace.Messages.Tasks;
using Shouldly;

namespace CodeSpace.UnitTests.Workflows;

/// <summary>
/// Pins the generic projection registry — the SAME IEnumerable&lt;T&gt;+dedup+resolve shape
/// <see cref="AgentHarnessRegistryTests"/> pins for harnesses. Dispatch is <c>Resolve(openString)</c> with no
/// per-kind switch: a builder is resolvable purely by registering its class, a duplicate kind is rejected in
/// the ctor, and an unknown kind throws (or TryResolve returns false). This is half the zero-core-edit proof —
/// the registry never names a concrete projection kind.
/// </summary>
[Trait("Category", "Unit")]
public class TaskProjectionRegistryTests
{
    private sealed class FakeBuilder : IWorkflowDefinitionBuilder
    {
        public FakeBuilder(string projectionKind) => ProjectionKind = projectionKind;

        public string ProjectionKind { get; }

        public WorkflowDefinition Build(TaskBuildContext context) => new()
        {
            Nodes = new List<NodeDefinition>(),
            Edges = new List<EdgeDefinition>(),
        };
    }

    [Fact]
    public void Resolves_a_registered_builder_by_kind()
    {
        var single = new FakeBuilder(TaskProjectionKinds.SingleAgent);
        var registry = new TaskProjectionRegistry(new[] { single });

        registry.Resolve(TaskProjectionKinds.SingleAgent).ShouldBeSameAs(single);
    }

    [Fact]
    public void Resolve_throws_for_an_unregistered_kind()
    {
        var registry = new TaskProjectionRegistry(new[] { new FakeBuilder(TaskProjectionKinds.SingleAgent) });

        Should.Throw<InvalidOperationException>(() => registry.Resolve("never-registered"));
    }

    [Fact]
    public void Construction_rejects_duplicate_kinds()
    {
        Should.Throw<InvalidOperationException>(() =>
            new TaskProjectionRegistry(new[] { new FakeBuilder("same"), new FakeBuilder("same") }));
    }

    [Fact]
    public void Kinds_lists_every_registered_builder()
    {
        var registry = new TaskProjectionRegistry(new[] { new FakeBuilder("a"), new FakeBuilder("b") });

        registry.Kinds.ShouldBe(new[] { "a", "b" }, ignoreOrder: true);
    }

    [Fact]
    public void TryResolve_returns_true_with_the_builder_when_registered()
    {
        var single = new FakeBuilder(TaskProjectionKinds.SingleAgent);
        var registry = new TaskProjectionRegistry(new[] { single });

        registry.TryResolve(TaskProjectionKinds.SingleAgent, out var builder).ShouldBeTrue();
        builder.ShouldBeSameAs(single);
    }

    [Fact]
    public void TryResolve_returns_false_for_an_unregistered_kind()
    {
        var registry = new TaskProjectionRegistry(new[] { new FakeBuilder(TaskProjectionKinds.SingleAgent) });

        registry.TryResolve("never-registered", out var builder).ShouldBeFalse();
        builder.ShouldBeNull();
    }
}
