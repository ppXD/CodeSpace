using System.Text.Json;
using CodeSpace.Core.Services.Workflows.Nodes;
using CodeSpace.Core.Services.Workflows.Nodes.Builtin;
using CodeSpace.Core.Services.Workflows.Runtime;
using CodeSpace.Messages.Enums;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace CodeSpace.UnitTests.Workflows;

[Trait("Category", "Unit")]
public class LogicIfNodeTests
{
    [Fact]
    public void Manifest_declares_true_and_false_output_handles()
    {
        var manifest = new LogicIfNode().Manifest;
        manifest.Outputs.ShouldNotBeNull();
        manifest.Outputs!.Select(o => o.Name).ShouldBe(new[] { "true", "false" });
    }

    [Theory]
    [InlineData("{{trigger.number}} > 10", "true")]
    [InlineData("{{trigger.number}} > 100", "false")]
    public async Task Routes_to_matching_handle(string condition, string expectedHandle)
    {
        var node = new LogicIfNode();
        var context = BuildContext(condition);

        var result = await node.RunAsync(context, CancellationToken.None);

        result.Status.ShouldBe(NodeStatus.Success);
        result.RoutingHints.ShouldNotBeNull();
        result.RoutingHints!.ShouldHaveSingleItem();
        result.RoutingHints![0].ShouldBe(expectedHandle);
    }

    [Fact]
    public async Task Sets_matched_output_flag()
    {
        var result = await new LogicIfNode().RunAsync(BuildContext("{{trigger.number}} > 10"), CancellationToken.None);

        result.Outputs["matched"].GetBoolean().ShouldBeTrue();
    }

    [Fact]
    public async Task Fails_when_condition_missing()
    {
        var ctx = new NodeRunContext
        {
            Inputs = new Dictionary<string, JsonElement>(),
            Config = new Dictionary<string, JsonElement>(),
            RawInputs = JsonDocument.Parse("{}").RootElement,
            RawConfig = JsonDocument.Parse("{}").RootElement,
            Scope = new NodeRunScope { Trigger = new Dictionary<string, JsonElement>() },
            Logger = NullLogger.Instance,
            Observability = NodeObservability.NoOp,
        };

        var result = await new LogicIfNode().RunAsync(ctx, CancellationToken.None);

        result.Status.ShouldBe(NodeStatus.Failure);
        result.Error.ShouldContain("condition");
    }

    private static NodeRunContext BuildContext(string condition)
    {
        var trigger = JsonDocument.Parse("""{"number":42}""").RootElement
            .EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone());

        var config = new Dictionary<string, JsonElement>
        {
            ["condition"] = JsonSerializer.SerializeToElement(condition)
        };

        return new NodeRunContext
        {
            Inputs = new Dictionary<string, JsonElement>(),
            Config = config,
            RawInputs = JsonDocument.Parse("{}").RootElement,
            RawConfig = JsonDocument.Parse($$"""{ "condition": {{JsonSerializer.Serialize(condition)}} }""").RootElement,
            Scope = new NodeRunScope { Trigger = trigger },
            Logger = NullLogger.Instance,
            Observability = NodeObservability.NoOp,
        };
    }
}
