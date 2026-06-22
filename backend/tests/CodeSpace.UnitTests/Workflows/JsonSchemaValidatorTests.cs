using System.Text.Json;
using CodeSpace.Core.Services.Workflows.Llm;
using Shouldly;

namespace CodeSpace.UnitTests.Workflows;

/// <summary>
/// Pins the focused structured-output validator: it REJECTS the real LLM-output failures (a missing required field, a
/// wrong-typed value, an invalid enum — recursively) and is LENIENT on what it doesn't model (extra fields, $ref,
/// oneOf) so it never false-rejects a valid-but-richer object. This is what stops a "{}" / "no kind" object from
/// silently passing as success.
/// </summary>
[Trait("Category", "Unit")]
public class JsonSchemaValidatorTests
{
    private static JsonElement S(string json) => JsonDocument.Parse(json).RootElement;

    [Fact]
    public void A_conforming_object_has_no_errors()
    {
        var schema = S("""{"type":"object","required":["kind"],"properties":{"kind":{"type":"string","enum":["plan","merge"]}}}""");
        JsonSchemaValidator.Validate(S("""{"kind":"plan"}"""), schema).ShouldBeEmpty();
    }

    [Fact]
    public void A_missing_required_property_is_an_error()
    {
        // The supervisor "no kind" failure: an object missing the required field must NOT pass as success.
        var schema = S("""{"type":"object","required":["kind"],"properties":{"kind":{"type":"string"}}}""");
        var errors = JsonSchemaValidator.Validate(S("""{"summary":"hi"}"""), schema);

        errors.ShouldHaveSingleItem().ShouldContain("missing required property 'kind'");
    }

    [Fact]
    public void An_empty_object_against_a_required_schema_fails()
    {
        JsonSchemaValidator.Validate(S("{}"), S("""{"type":"object","required":["a","b"]}""")).Count.ShouldBe(2);
    }

    [Fact]
    public void A_wrong_typed_value_is_an_error()
    {
        var errors = JsonSchemaValidator.Validate(S("""{"n":"not-a-number"}"""), S("""{"type":"object","properties":{"n":{"type":"number"}}}"""));
        errors.ShouldHaveSingleItem().ShouldContain("expected type 'number'");
    }

    [Fact]
    public void An_invalid_enum_value_is_an_error()
    {
        var errors = JsonSchemaValidator.Validate(S("""{"kind":"explode"}"""), S("""{"type":"object","properties":{"kind":{"enum":["plan","merge","stop"]}}}"""));
        errors.ShouldHaveSingleItem().ShouldContain("not one of the allowed enum");
    }

    [Fact]
    public void Nested_objects_and_array_items_are_validated_recursively()
    {
        var schema = S("""
            {"type":"object","required":["items"],"properties":{
              "items":{"type":"array","items":{"type":"object","required":["id"],"properties":{"id":{"type":"string"}}}}}}
            """);
        // Second array item is missing its required id.
        var errors = JsonSchemaValidator.Validate(S("""{"items":[{"id":"a"},{"name":"b"}]}"""), schema);

        errors.ShouldHaveSingleItem().ShouldContain("$.items[1]: missing required property 'id'");
    }

    [Fact]
    public void It_is_lenient_on_extra_fields_and_unmodelled_keywords()
    {
        // An extra field (no additionalProperties:false enforced), a oneOf/$ref it doesn't model, and an integer that
        // satisfies number — all pass. The validator catches garbage, it is NOT a strict conformance oracle.
        var schema = S("""{"type":"object","required":["kind"],"properties":{"kind":{"type":"string"}},"oneOf":[{"x":1}]}""");
        JsonSchemaValidator.Validate(S("""{"kind":"plan","extra":42,"more":{"nested":true}}"""), schema).ShouldBeEmpty();

        JsonSchemaValidator.Validate(S("""{"n":7}"""), S("""{"type":"object","properties":{"n":{"type":"integer"}}}""")).ShouldBeEmpty();
    }
}
