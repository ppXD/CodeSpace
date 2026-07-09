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

    // ── JSON repair: recover a TRUNCATED / trailing-broken object (the #1 real cause — a long output cut at the token
    //    cap). Repair runs ONLY after the strict parse fails, and the recovered object still faces schema validation
    //    downstream, so it can never turn a working case bad — it only rescues a near-valid object from a hard failure.

    [Theory]
    // Truncated mid-string → close the string + the object.
    [InlineData("""{"kind":"stop","why":"the work is fully compl""", "kind", "stop")]
    // Truncated right after a complete value → just close the object.
    [InlineData("""{"kind":"merge","count":3""", "kind", "merge")]
    // A trailing comma (a common model tic) before truncation → drop it + close.
    [InlineData("""{"kind":"retry","note":"ok",""", "kind", "retry")]
    // A dangling `"key":` with no value → drop the dangling pair + a preceding comma, then close.
    [InlineData("""{"kind":"plan","retry":""", "kind", "plan")]
    // Truncated inside a nested array → close the string, the array, and the object.
    [InlineData("""{"kind":"plan","items":["a","b""", "kind", "plan")]
    // Truncated deep inside a nested object → close everything up the stack.
    [InlineData("""{"kind":"stop","rationale":{"why":"done","evidence":"verified""", "kind", "stop")]
    public void Repairs_a_truncated_or_trailing_broken_object(string content, string key, string expected) =>
        StructuredJsonText.TryExtractObject(content)!.Value.GetProperty(key).GetString().ShouldBe(expected);

    [Fact]
    public void Repair_recovers_a_nested_field_from_a_truncated_object()
    {
        var obj = StructuredJsonText.TryExtractObject(
            """{"kind":"stop","rationale":{"why":"the merge succeeded with the patch artifact""")!.Value;

        obj.GetProperty("kind").GetString().ShouldBe("stop", "the completed leading fields survive the repair");
        obj.GetProperty("rationale").GetProperty("why").GetString().ShouldStartWith("the merge succeeded");
    }

    [Fact]
    public void Repair_never_alters_a_strictly_valid_object() =>
        // A complete, valid object (even with trailing prose) parses strictly — repair is never invoked, value verbatim.
        StructuredJsonText.TryExtractObject("""{"kind":"plan","n":5} then some prose""")!
            .Value.GetProperty("n").GetInt32().ShouldBe(5);

    [Fact]
    public void Repair_still_returns_null_for_unrecoverable_garbage()
    {
        // The pre-existing contract holds: content with no recoverable object structure stays null — repair never
        // invents data that would pass as a real decision.
        StructuredJsonText.TryExtractObject("not { valid").ShouldBeNull();
        StructuredJsonText.TryExtractObject("prose with no object at all").ShouldBeNull();
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
