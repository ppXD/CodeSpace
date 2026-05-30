using System.Text.Json;
using CodeSpace.Core.Services.Workflows.Nodes;
using CodeSpace.Core.Services.Workflows.Nodes.Builtin;
using CodeSpace.Messages.Enums;
using Shouldly;

namespace CodeSpace.UnitTests.Workflows;

/// <summary>
/// The two node types behind a loop container. <c>flow.loop</c> declares <see cref="NodeKind.Loop"/>
/// (the engine dispatches on Kind, never calls its RunAsync) and a config schema the inspector
/// renders; <c>flow.loop_start</c> is the body's passthrough entry marker.
/// </summary>
[Trait("Category", "Unit")]
public class FlowLoopNodeTests
{
    [Fact]
    public void Loop_node_declares_the_Loop_kind_and_a_renderable_config_schema()
    {
        var node = new FlowLoopNode();

        node.TypeKey.ShouldBe("flow.loop");
        node.Manifest.Kind.ShouldBe(NodeKind.Loop);
        node.Manifest.ConfigSchema.ValueKind.ShouldBe(JsonValueKind.Object);
        // The schema advertises the three inspector sections.
        var props = node.Manifest.ConfigSchema.GetProperty("properties");
        props.TryGetProperty("loopVariables", out _).ShouldBeTrue();
        props.TryGetProperty("termination", out _).ShouldBeTrue();
        props.TryGetProperty("maxIterations", out _).ShouldBeTrue();
    }

    [Fact]
    public void Loop_node_RunAsync_throws_because_it_is_engine_driven()
    {
        // Reaching RunAsync means the Kind=Loop dispatch was bypassed — surface it, don't no-op.
        var node = new FlowLoopNode();
        Should.Throw<InvalidOperationException>(() => node.RunAsync(null!, CancellationToken.None));
    }

    [Fact]
    public async Task Loop_start_is_a_passthrough_that_emits_nothing()
    {
        var node = new FlowLoopStartNode();

        node.TypeKey.ShouldBe("flow.loop_start");
        node.Manifest.Kind.ShouldBe(NodeKind.Regular);

        var result = await node.RunAsync(null!, CancellationToken.None);
        result.Status.ShouldBe(NodeStatus.Success);
        result.Outputs.ShouldBeEmpty();
    }
}
