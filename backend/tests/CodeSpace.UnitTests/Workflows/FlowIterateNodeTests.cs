using System.Text.Json;
using CodeSpace.Core.Services.Workflows.Nodes;
using CodeSpace.Core.Services.Workflows.Nodes.Builtin;
using CodeSpace.Core.Services.Workflows.Runtime;
using CodeSpace.Messages.Enums;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace CodeSpace.UnitTests.Workflows;

public class FlowIterateNodeTests
{
    [Fact]
    public async Task Maps_array_of_objects_via_template()
    {
        var items = """[ { "name": "a" }, { "name": "b" }, { "name": "c" } ]""";
        var result = await Run(items, "hello {{item.name}}");

        result.Status.ShouldBe(NodeStatus.Success);
        result.Outputs["count"].GetInt32().ShouldBe(3);

        var results = result.Outputs["results"];
        results.GetArrayLength().ShouldBe(3);
        results[0].GetString().ShouldBe("hello a");
        results[1].GetString().ShouldBe("hello b");
        results[2].GetString().ShouldBe("hello c");
    }

    [Fact]
    public async Task Exposes_index_to_template()
    {
        var items = """[ "x", "y", "z" ]""";
        var result = await Run(items, "{{index}}:{{item}}");

        var results = result.Outputs["results"];
        results[0].GetString().ShouldBe("0:x");
        results[2].GetString().ShouldBe("2:z");
    }

    [Fact]
    public async Task Custom_itemAs_name()
    {
        var items = """[ 1, 2, 3 ]""";
        var ctx = BuildContext(items, "doubled = {{element}}");
        ctx = ctx with { Inputs = new Dictionary<string, JsonElement>(ctx.Inputs) { ["itemAs"] = JsonSerializer.SerializeToElement("element") } };

        var result = await new FlowIterateNode().RunAsync(ctx, CancellationToken.None);

        result.Outputs["results"][0].GetString().ShouldBe("doubled = 1");
    }

    [Fact]
    public async Task Empty_array_yields_empty_results()
    {
        var result = await Run("[]", "{{item}}");
        result.Outputs["count"].GetInt32().ShouldBe(0);
        result.Outputs["results"].GetArrayLength().ShouldBe(0);
    }

    [Fact]
    public async Task Fails_when_items_is_not_array()
    {
        var ctx = BuildContext("\"not an array\"", "x");
        // Manually rewrite items input to a non-array shape
        ctx = ctx with { Inputs = new Dictionary<string, JsonElement>(ctx.Inputs)
        {
            ["items"] = JsonSerializer.SerializeToElement("not an array")
        }};

        var result = await new FlowIterateNode().RunAsync(ctx, CancellationToken.None);

        result.Status.ShouldBe(NodeStatus.Failure);
        result.Error.ShouldContain("array");
    }

    [Fact]
    public async Task Fails_when_template_missing()
    {
        var ctx = BuildContext("[1,2,3]", "");
        var result = await new FlowIterateNode().RunAsync(ctx, CancellationToken.None);

        result.Status.ShouldBe(NodeStatus.Failure);
        result.Error.ShouldContain("template");
    }

    [Fact]
    public async Task Iteration_does_not_mutate_outer_scope()
    {
        var outerScope = new NodeRunScope
        {
            Trigger = JsonDocument.Parse("""{"original":"untouched"}""").RootElement
                .EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone())
        };

        var ctx = BuildContextWithScope("""[1,2]""", "{{item}}", outerScope);
        await new FlowIterateNode().RunAsync(ctx, CancellationToken.None);

        outerScope.Trigger.ShouldContainKey("original");
        outerScope.Trigger.ShouldNotContainKey("item");
        outerScope.Trigger.ShouldNotContainKey("index");
    }

    private static Task<NodeResult> Run(string itemsJson, string template) =>
        new FlowIterateNode().RunAsync(BuildContext(itemsJson, template), CancellationToken.None);

    private static NodeRunContext BuildContext(string itemsJson, string template)
    {
        var scope = new NodeRunScope { Trigger = new Dictionary<string, JsonElement>() };
        return BuildContextWithScope(itemsJson, template, scope);
    }

    private static NodeRunContext BuildContextWithScope(string itemsJson, string template, NodeRunScope scope)
    {
        // Build the raw JSON shape the engine would hand us — config/inputs as a JsonElement
        // object holding the un-resolved values. The iterate node reads `template` via RawConfig
        // so it can substitute per-iteration. Tests must mirror the engine's input shape.
        var rawConfigJson = JsonSerializer.Serialize(new { template });
        var rawInputsJson = $$"""{ "items": {{itemsJson}} }""";

        return new NodeRunContext
        {
            Inputs = new Dictionary<string, JsonElement>
            {
                ["items"] = JsonDocument.Parse(itemsJson).RootElement.Clone()
            },
            Config = new Dictionary<string, JsonElement>
            {
                ["template"] = JsonSerializer.SerializeToElement(template)
            },
            RawConfig = JsonDocument.Parse(rawConfigJson).RootElement.Clone(),
            RawInputs = JsonDocument.Parse(rawInputsJson).RootElement.Clone(),
            Scope = scope,
            Logger = NullLogger.Instance,
            Observability = NodeObservability.NoOp,
        };
    }
}
