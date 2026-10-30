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

    [Theory]
    [InlineData("{\"a\":1,\"a\":2}")]
    [InlineData("{\"a\\u0000\":1}")]
    [InlineData("{\"a\":\"\\u0000\"}")]
    [InlineData("{\"\\uD800\":1}")]
    [InlineData("{\"a\":1e-331}")]
    [InlineData("{\"a\":0e331}")]
    public void Unsafe_payloads_are_rejected_before_schema_checks(string body) =>
        ExternalTriggerPayloadValidator.Validate(Schema(body), Schema("{\"type\":\"object\"}")).ShouldNotBeEmpty();

    [Fact]
    public void Representability_and_schema_have_independent_node_budgets()
    {
        var body = "[" + string.Join(",", Enumerable.Repeat("1", 9999)) + "]";
        ExternalTriggerPayloadValidator.Validate(Schema(body), Schema("{\"type\":\"array\",\"items\":{\"type\":\"integer\"}}")).ShouldBeEmpty();
    }

    [Theory]
    [InlineData("{\"type\":\"object\",\"required\":3}")]
    [InlineData("{\"type\":\"object\",\"enum\":[]}")]
    [InlineData("{\"type\":\"object\",\"properties\":[]}")]
    [InlineData("{\"type\":\"object\",\"items\":7}")]
    [InlineData("{\"type\":\"object\",\"additionalProperties\":7}")]
    [InlineData("{\"type\":\"object\",\"maxLength\":-1}")]
    [InlineData("{\"type\":\"object\",\"description\":1}")]
    [InlineData("{\"type\":\"object\",\"type\":\"object\"}")]
    [InlineData("[]")]
    public void Malformed_schema_shapes_cannot_be_saved(string schema) =>
        ExternalTriggerPayloadValidator.ValidateSchema(Schema(schema)).ShouldNotBeEmpty();

    [Fact]
    public void Editor_annotations_are_accepted() => ExternalTriggerPayloadValidator.ValidateSchema(Schema(
        "{\"type\":\"object\",\"properties\":{\"text\":{\"type\":\"string\",\"x-long\":true,\"x-hidden\":true,\"x-selector\":\"repository\"}}}")).ShouldBeEmpty();

    [Theory]
    [InlineData("{\"type\":\"object\",\"additionalProperties\":{\"type\":\"string\"}}")]
    [InlineData("{\"type\":\"object\",\"properties\":{\"a\":false}}")]
    public void Additional_properties_and_boolean_schemas_are_enforced(string schema) =>
        ExternalTriggerPayloadValidator.Validate(Schema("{\"a\":1}"), Schema(schema)).ShouldNotBeEmpty();

    [Fact]
    public void Numeric_enum_comparison_does_not_round_large_fractional_values() =>
        ExternalTriggerPayloadValidator.Validate(Schema("9007199254740992.1"),
            Schema("{\"type\":\"number\",\"enum\":[9007199254740992]}")).ShouldNotBeEmpty();

    [Theory]
    [InlineData(256, true)]
    [InlineData(257, false)]
    public void Number_token_length_is_bounded_before_integer_validation(int length, bool valid)
    {
        var number = "1" + new string('0', length - 1);
        (ExternalTriggerPayloadValidator.Validate(Schema(number), Schema("{\"type\":\"integer\"}")).Count == 0).ShouldBe(valid);
    }

    [Theory]
    [InlineData("1e-330", true)]
    [InlineData("0e330", true)]
    [InlineData("1e-331", false)]
    [InlineData("0e331", false)]
    [InlineData("1e000000000000000000000000000001", true)]
    public void Explicit_exponent_values_have_a_bounded_range(string token, bool valid) =>
        (ExternalTriggerPayloadValidator.Validate(Schema(token), Schema("{\"type\":\"number\"}")).Count == 0).ShouldBe(valid);

    [Fact]
    public void A_payload_over_the_node_budget_is_refused()
    {
        var body = "[" + string.Join(",", Enumerable.Repeat("1", 10_000)) + "]";
        ExternalTriggerPayloadValidator.ValidatePayload(Schema(body)).ShouldContain(e => e.Contains("too many nodes"));
    }

    [Fact]
    public void Schema_errors_are_also_bounded()
    {
        var properties = string.Join(",", Enumerable.Range(0, 100).Select(i => $"\"x{i}\":{{\"unsupported\":true}}"));
        ExternalTriggerPayloadValidator.ValidateSchema(Schema($"{{\"type\":\"object\",\"properties\":{{{properties}}}}}"))
            .Count.ShouldBeLessThanOrEqualTo(20);
    }

    [Fact]
    public void Enum_comparisons_have_a_bounded_total_work_budget()
    {
        const int enumSize = 6_000;
        var values = string.Join(",", Enumerable.Range(0, enumSize));
        var payload = "[" + string.Join(",", Enumerable.Repeat("-1", enumSize)) + "]";
        var schema = "{\"type\":\"array\",\"items\":{\"type\":\"integer\",\"enum\":[" + values + "]}}";
        var errors = ExternalTriggerPayloadValidator.Validate(Schema(payload), Schema(schema));
        errors.ShouldContain(e => e.Contains("enum comparison budget"));
        errors.Count.ShouldBeLessThanOrEqualTo(ExternalTriggerPayloadValidator.MaxErrors);
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

    [Theory]
    [InlineData("\"text\"", true)]
    [InlineData("7", true)]
    [InlineData("true", true)]
    [InlineData("null", false)]
    [InlineData("{}", false)]
    public void Array_items_accept_a_union_of_json_types(string json, bool isValid)
    {
        var errors = ExternalTriggerPayloadValidator.Validate(
            Schema($"[{json}]"),
            Schema("""{ "type": "array", "items": { "type": ["string", "number", "boolean"] } }"""));
        (errors.Count == 0).ShouldBe(isValid);
    }

    [Fact]
    public void An_empty_type_union_is_refused_with_a_usable_error()
    {
        var errors = ExternalTriggerPayloadValidator.Validate(
            Schema("\"text\""),
            Schema("""{ "type": [] }"""));
        errors.ShouldHaveSingleItem().ShouldContain("the schema declares none");
    }

    [Fact]
    public void An_unknown_type_keyword_is_refused_instead_of_silently_matching()
    {
        var errors = ExternalTriggerPayloadValidator.ValidateSchema(Schema("""{ "type": "sting" }"""));
        errors.ShouldContain(e => e.Contains("sting") && e.Contains("not supported"));
    }

    [Fact]
    public void Validation_stops_after_a_bounded_number_of_errors()
    {
        var errors = ExternalTriggerPayloadValidator.Validate(
            Schema("[" + string.Join(",", Enumerable.Repeat("1", 100)) + "]"),
            Schema("""{ "type": "array", "items": { "type": "string" } }"""));
        errors.Count.ShouldBe(ExternalTriggerPayloadValidator.MaxErrors);
    }

    [Fact]
    public void An_excessively_long_integer_exponent_is_refused_without_parsing_it()
    {
        var exponent = new string('9', 100_000);
        var errors = ExternalTriggerPayloadValidator.Validate(
            Schema("1e" + exponent),
            Schema("""{ "type": "integer" }"""));
        errors.ShouldContain(e => e.Contains("number") && e.Contains("too long"));
    }

    [Fact]
    public void A_number_postgres_cannot_represent_is_refused()
    {
        var errors = ExternalTriggerPayloadValidator.Validate(
            Schema("1e200000"),
            Schema("""{ "type": "object" }"""));
        errors.ShouldContain(e => e.Contains("not representable"));
    }

    [Fact]
    public void An_unpaired_surrogate_is_refused_without_reading_an_invalid_string()
    {
        var errors = ExternalTriggerPayloadValidator.Validate(
            Schema("\"\\uD800\""),
            Schema("""{ "type": "string", "maxLength": 10 }"""));
        errors.ShouldContain(e => e.Contains("not representable"));
    }

    [Fact]
    public void An_unpaired_surrogate_does_not_turn_enum_validation_into_a_server_error()
    {
        var errors = ExternalTriggerPayloadValidator.Validate(
            Schema("\"\\uD800\""),
            Schema("""{ "type": "string", "enum": ["ok"] }"""));
        errors.ShouldContain(e => e.Contains("not representable"));
    }

    [Fact]
    public void An_unpaired_surrogate_nested_in_an_enum_value_does_not_escape_as_a_server_error()
    {
        var errors = ExternalTriggerPayloadValidator.Validate(
            Schema("""[{ "text": "\uD800" }]"""),
            Schema("""{ "type": "array", "items": { "type": "object", "enum": [{ "text": "ok" }] } }"""));
        errors.ShouldContain(e => e.Contains("not representable"));
    }

    [Theory]
    [InlineData("""{ "type": "object", "properties": { "x": { "pattern": "^a" } } }""")]
    [InlineData("""{ "type": "object", "properties": { "x": { "oneOf": [{ "type": "string" }] } } }""")]
    [InlineData("""{ "type": "object", "properties": { "x": { "$ref": "#/$defs/x" } } }""")]
    [InlineData("""{ "type": "object", "additionalProperties": { "type": "string", "pattern": "^a" } }""")]
    public void Unsupported_schema_keywords_are_refused_at_save_validation(string schema)
    {
        var errors = ExternalTriggerPayloadValidator.ValidateSchema(Schema(schema));
        errors.ShouldContain(e => e.Contains("not supported"));
    }

    [Fact]
    public void A_schema_deeper_than_the_limit_is_refused()
    {
        var schema = """{ "type": "object", "properties": { "x": """
            + string.Concat(Enumerable.Repeat("""{ "type": "array", "items": """, ExternalTriggerPayloadValidator.MaxSchemaDepth + 1))
            + """{ "type": "string" }"""
            + string.Concat(Enumerable.Repeat(" }", ExternalTriggerPayloadValidator.MaxSchemaDepth + 1))
            + """ } }""";
        ExternalTriggerPayloadValidator.ValidateSchema(Schema(schema, maxDepth: 256)).ShouldContain(e => e.Contains("too deep"));
    }

    [Fact]
    public void A_payload_deeper_than_the_limit_is_refused_before_representability_recursion()
    {
        var body = string.Concat(Enumerable.Repeat("[", ExternalTriggerPayloadValidator.MaxSchemaDepth + 2))
            + string.Concat(Enumerable.Repeat("]", ExternalTriggerPayloadValidator.MaxSchemaDepth + 2));
        var errors = ExternalTriggerPayloadValidator.Validate(
            Schema(body, maxDepth: 256),
            Schema("""{ "type": "array" }"""));
        errors.ShouldContain(e => e.Contains("too deep"));
    }

    private static JsonElement Schema(string json, int maxDepth = 64) =>
        JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = maxDepth }).RootElement.Clone();
}
