using System.Text.Json;
using CodeSpace.Core.Services.Workflows.ExternalTriggers;
using CodeSpace.Messages.Dtos.Workflows;
using Shouldly;

namespace CodeSpace.UnitTests.Workflows;

[Trait("Category", "Unit")]
public sealed class ExternalTriggerInputProjectionTests
{
    [Fact]
    public void The_declared_schema_becomes_the_workflow_inputs()
    {
        var inputs = ExternalTriggerInputProjection.Apply([], DefinitionWith("""
            { "slug": "a", "inputSchema": {
                "type": "object",
                "properties": {
                  "service": { "type": "string", "title": "Service", "description": "Which service" },
                  "count":   { "type": "integer", "default": 1 }
                },
                "required": ["service"] } }
            """));

        inputs.Select(i => i.Name).ShouldBe(["service", "count"]);
        var service = inputs[0];
        service.Label.ShouldBe("Service");
        service.Description.ShouldBe("Which service");
        service.Required.ShouldBeTrue();
        service.Default.ShouldBeNull();
        service.Schema.GetProperty("type").GetString().ShouldBe("string");
        service.Schema.GetProperty("title").GetString().ShouldBe("Service");
        service.Schema.GetProperty("description").GetString().ShouldBe("Which service");
        inputs[1].Required.ShouldBeFalse();
        inputs[1].Label.ShouldBeNull();
        inputs[1].Description.ShouldBeNull();
        inputs[1].Default!.Value.GetInt32().ShouldBe(1);
    }

    [Fact]
    public void A_workflow_without_an_external_node_keeps_its_inputs_as_declared()
    {
        var declared = new[] { new WorkflowVariable { Name = "x", Schema = Json("""{"type":"string"}""") } };
        var definition = new WorkflowDefinition { Nodes = [], Edges = [] };

        ExternalTriggerInputProjection.Apply(declared, definition).ShouldBeSameAs(declared);
    }

    [Theory]
    [InlineData("""{ "slug": "a" }""")]
    [InlineData("""{ "inputSchema": null }""")]
    [InlineData("""{ "inputSchema": [] }""")]
    [InlineData("""{ "inputSchema": { "type": "object" } }""")]
    [InlineData("""{ "inputSchema": { "properties": [] } }""")]
    public void A_node_without_object_properties_declares_nothing(string config)
    {
        var declared = new[] { new WorkflowVariable { Name = "stale", Schema = Json("""{"type":"string"}""") } };

        ExternalTriggerInputProjection.Apply(declared, DefinitionWith(config)).ShouldBeEmpty();
    }

    [Fact]
    public void A_property_with_no_type_is_skipped_not_fabricated() =>
        ExternalTriggerInputProjection.Apply([], DefinitionWith(
            """{ "inputSchema": { "type": "object", "properties": { "x": { } } } }""")).ShouldBeEmpty();

    [Fact]
    public void The_node_schema_replaces_stale_inputs_and_preserves_the_full_property_schema()
    {
        var declared = new[] { new WorkflowVariable { Name = "stale", Schema = Json("""{"type":"string"}""") } };
        var definition = DefinitionWith("""
            { "inputSchema": { "properties": {
              "Service_ID": { "type": "array", "items": { "type": "string", "enum": ["api", "worker"] }, "default": ["api"] }
            }, "required": ["service_id"] } }
            """);

        var inputs = ExternalTriggerInputProjection.Apply(declared, definition);

        inputs.Count.ShouldBe(1);
        inputs[0].Name.ShouldBe("Service_ID");
        inputs[0].Required.ShouldBeFalse();
        inputs[0].Schema.GetProperty("items").GetProperty("enum").EnumerateArray()
            .Select(v => v.GetString()).ShouldBe(["api", "worker"]);
        inputs[0].Default!.Value[0].GetString().ShouldBe("api");
        definition.Inputs.ShouldBeEmpty();
    }

    private static WorkflowDefinition DefinitionWith(string configJson) => new()
    {
        Nodes = [new NodeDefinition { Id = "ext", TypeKey = "trigger.external", Config = Json(configJson) }],
        Edges = [],
    };

    private static JsonElement Json(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
}
