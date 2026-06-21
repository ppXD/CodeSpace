using System.Text.Json;
using CodeSpace.Core.Services.Workflows.Llm;
using Shouldly;

namespace CodeSpace.UnitTests.Workflows;

/// <summary>
/// Pins the structured-output fallback helpers — the generic path that lets a model which IGNORES forced
/// tool/function calling still drive a structured node by returning JSON as text. The content recovery must handle
/// a bare object, a markdown fence, and surrounding prose, and refuse non-JSON.
/// </summary>
[Trait("Category", "Unit")]
public class StructuredJsonTextTests
{
    [Fact]
    public void Extracts_a_bare_json_object() =>
        StructuredJsonText.TryExtractObject("""{"kind":"plan"}""")!.Value.GetProperty("kind").GetString().ShouldBe("plan");

    [Fact]
    public void Extracts_from_a_markdown_fence() =>
        StructuredJsonText.TryExtractObject("```json\n{\"kind\":\"merge\"}\n```")!.Value.GetProperty("kind").GetString().ShouldBe("merge");

    [Fact]
    public void Extracts_the_object_from_surrounding_prose() =>
        StructuredJsonText.TryExtractObject("Sure — here is the decision: {\"kind\":\"retry\",\"retry\":{\"subtaskId\":\"s2\"}} . Let me know.")!
            .Value.GetProperty("retry").GetProperty("subtaskId").GetString().ShouldBe("s2");

    [Fact]
    public void Returns_null_when_there_is_no_json_object()
    {
        StructuredJsonText.TryExtractObject("I cannot do that.").ShouldBeNull();
        StructuredJsonText.TryExtractObject("not { valid").ShouldBeNull();
        StructuredJsonText.TryExtractObject("").ShouldBeNull();
        StructuredJsonText.TryExtractObject(null).ShouldBeNull();
    }

    [Fact]
    public void With_schema_instruction_appends_the_schema_and_a_json_only_directive()
    {
        var schema = JsonDocument.Parse("""{"type":"object","properties":{"kind":{"type":"string"}}}""").RootElement;

        var prompt = StructuredJsonText.WithSchemaInstruction("the base system prompt", schema);

        prompt.ShouldContain("the base system prompt", customMessage: "the original prompt is preserved");
        prompt.ShouldContain("JSON object", customMessage: "the JSON-only directive is added");
        prompt.ShouldContain("\"kind\"", customMessage: "the schema is carried into the prompt for non-tool models");
    }
}
