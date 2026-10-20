using System.Text.Json;
using CodeSpace.Core.Services.Workflows.ExternalTriggers;
using Shouldly;

namespace CodeSpace.UnitTests.Workflows;

[Trait("Category", "Unit")]
public class ExternalTriggerPayloadValidatorTests
{
    private const string Shape = """
        { "type": "object",
          "properties": { "service": { "type": "string" }, "count": { "type": "integer" } },
          "required": ["service"],
          "additionalProperties": false }
        """;

    [Fact]
    public void A_conforming_body_passes()
    {
        ExternalTriggerPayloadValidator.Validate(
            Schema("""{ "service": "checkout", "count": 3 }"""), Schema(Shape)).ShouldBeEmpty();
    }

    [Fact]
    public void A_missing_required_field_is_named()
    {
        var errors = ExternalTriggerPayloadValidator.Validate(Schema("""{ "count": 1 }"""), Schema(Shape));
        errors.ShouldContain(e => e.Contains("$.service") && e.Contains("required"));
    }

    [Fact]
    public void A_wrong_type_is_named()
    {
        var errors = ExternalTriggerPayloadValidator.Validate(Schema("""{ "service": 7 }"""), Schema(Shape));
        errors.ShouldContain(e => e.Contains("$.service"));
    }

    [Fact]
    public void A_large_fractional_number_is_not_an_integer()
    {
        var errors = ExternalTriggerPayloadValidator.Validate(
            Schema("""{ "service": "a", "count": 9007199254740992.1 }"""), Schema(Shape));
        errors.ShouldContain(e => e.Contains("$.count"));
    }

    [Theory]
    [InlineData("5.0")]
    [InlineData("1e2")]
    public void An_integer_value_with_decimal_or_exponent_notation_is_accepted(string number)
    {
        ExternalTriggerPayloadValidator.Validate(
            Schema($"{{ \"service\": \"a\", \"count\": {number} }}"), Schema(Shape)).ShouldBeEmpty();
    }

    [Fact]
    public void An_extra_property_is_refused_when_the_schema_closes_the_object()
    {
        var errors = ExternalTriggerPayloadValidator.Validate(
            Schema("""{ "service": "a", "unexpected": true }"""), Schema(Shape));
        errors.ShouldContain(e => e.Contains("$.unexpected"));
    }

    [Fact]
    public void An_extra_property_is_allowed_when_the_schema_leaves_it_open()
    {
        var errors = ExternalTriggerPayloadValidator.Validate(
            Schema("""{ "service": "a", "extra": 1 }"""),
            Schema("""{ "type": "object", "properties": { "service": { "type": "string" } } }"""));
        errors.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("object", "{}", true)]
    [InlineData("array", "[]", true)]
    [InlineData("string", "\"text\"", true)]
    [InlineData("integer", "3", true)]
    [InlineData("integer", "3.5", false)]
    [InlineData("integer", "1e-1", false)]
    [InlineData("number", "3.5", true)]
    [InlineData("boolean", "true", true)]
    [InlineData("null", "null", true)]
    public void Json_types_are_checked(string type, string json, bool isValid)
    {
        var errors = ExternalTriggerPayloadValidator.Validate(Schema(json), Schema($"{{ \"type\": \"{type}\" }}"));
        (errors.Count == 0).ShouldBe(isValid);
    }

    [Fact]
    public void An_enum_value_outside_the_contract_is_refused()
    {
        var errors = ExternalTriggerPayloadValidator.Validate(
            Schema("\"paused\""), Schema("""{ "type": "string", "enum": ["active", "disabled"] }"""));
        errors.ShouldContain(e => e.Contains("$"));
    }

    [Fact]
    public void A_string_over_max_length_is_refused()
    {
        var errors = ExternalTriggerPayloadValidator.Validate(
            Schema("\"long\""), Schema("""{ "type": "string", "maxLength": 3 }"""));
        errors.ShouldContain(e => e.Contains("$"));
    }

    [Fact]
    public void Array_items_are_validated_with_indexed_paths()
    {
        var errors = ExternalTriggerPayloadValidator.Validate(
            Schema("[\"ok\", 7]"), Schema("""{ "type": "array", "items": { "type": "string" } }"""));
        errors.ShouldContain(e => e.Contains("$[1]"));
    }

    private static JsonElement Schema(string json) => JsonDocument.Parse(json).RootElement.Clone();
}
