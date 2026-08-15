using System.Text.Json;
using CodeSpace.Core.Services.Workflows.Artifacts.Providers;
using Shouldly;

namespace CodeSpace.UnitTests.Workflows.Artifacts.Providers;

[Trait("Category", "Unit")]
public sealed class StorageProviderJsonTests
{
    [Fact]
    public void Required_type_additional_enum_and_pattern_assertions_are_enforced()
    {
        var schema = Json("""
            {
              "type": "object",
              "properties": {
                "accessKey": { "type": "string", "pattern": "^AK-[A-Z]{4}$" },
                "mode": { "type": "string", "enum": ["read", "write"] }
              },
              "required": ["accessKey", "mode"],
              "additionalProperties": false
            }
            """);

        StorageProviderJson.Validate(Json("""{ "accessKey": "AK-ABCD", "mode": "write" }"""), schema, "Secret");

        ShouldReject(schema, """{ "mode": "write" }""", "required");
        ShouldReject(schema, """{ "accessKey": 7, "mode": "write" }""", "type string");
        ShouldReject(schema, """{ "accessKey": "AK-ABCD", "mode": "write", "extra": true }""", "not allowed");
        ShouldReject(schema, """{ "accessKey": "AK-ABCD", "mode": "admin" }""", "allowed values");
        ShouldReject(schema, """{ "accessKey": "raw-secret", "mode": "write" }""", "pattern");
    }

    [Theory]
    [InlineData("oneOf")]
    [InlineData("uniqueItems")]
    [InlineData("exclusiveMinimum")]
    [InlineData("dependentRequired")]
    public void Unsupported_assertion_keywords_fail_closed(string keyword)
    {
        var schema = Json($$"""{ "type": "object", "{{keyword}}": true }""");

        var error = Should.Throw<ArgumentException>(() => StorageProviderJson.Validate(Json("{}"), schema, "Secret"));

        error.Message.ShouldContain(keyword);
        error.Message.ShouldContain("unsupported");
    }

    [Fact]
    public void Canonical_json_orders_object_properties_recursively_without_reordering_arrays()
    {
        var first = Json("""{ "z": [{ "b": 2, "a": 1 }], "a": "value" }""");
        var second = Json("""{ "a": "value", "z": [{ "a": 1, "b": 2 }] }""");

        StorageProviderJson.Canonicalize(first, "Secret").ShouldBe("""{"a":"value","z":[{"a":1,"b":2}]}""");
        StorageProviderJson.Canonicalize(second, "Secret").ShouldBe(StorageProviderJson.Canonicalize(first, "Secret"));
    }

    [Fact]
    public void Duplicate_properties_are_rejected_before_validation_or_encryption()
    {
        var schema = Json("""{ "type": "object", "additionalProperties": true }""");

        var error = Should.Throw<ArgumentException>(() => StorageProviderJson.Validate(Json("""{ "token": "first", "token": "second" }"""), schema, "Secret"));

        error.Message.ShouldContain("duplicate");
        error.Message.ShouldContain("token");
        error.Message.ShouldNotContain("first");
        error.Message.ShouldNotContain("second");
    }

    [Fact]
    public void Payload_byte_limit_is_utf8_exact_and_applies_before_canonicalization()
    {
        const int envelopeBytes = 12; // {"value":""}
        var exact = Json($$"""{"value":"{{new string('a', StorageProviderJson.MaxValueBytes - envelopeBytes)}}"}""");
        var oversizedAscii = Json($$"""{"value":"{{new string('a', StorageProviderJson.MaxValueBytes - envelopeBytes + 1)}}"}""");
        var oversizedUtf8 = Json($$"""{"value":"{{new string('界', StorageProviderJson.MaxValueBytes / 3)}}"}""");

        StorageProviderJson.Canonicalize(exact, "Secret").Length.ShouldBe(StorageProviderJson.MaxValueBytes);
        Should.Throw<ArgumentException>(() => StorageProviderJson.Canonicalize(oversizedAscii, "Secret")).Message.ShouldContain("bytes");
        Should.Throw<ArgumentException>(() => StorageProviderJson.Canonicalize(oversizedUtf8, "Secret")).Message.ShouldContain("UTF-8");
    }

    [Fact]
    public void Payload_depth_and_node_limits_fail_closed_without_recursing_unboundedly()
    {
        var allowedDepth = Json(new string('[', StorageProviderJson.MaxValueDepth - 1) + "0" + new string(']', StorageProviderJson.MaxValueDepth - 1));
        var excessiveDepth = Json(new string('[', StorageProviderJson.MaxValueDepth) + "0" + new string(']', StorageProviderJson.MaxValueDepth));
        var excessiveNodes = Json("[" + string.Join(',', Enumerable.Repeat("0", StorageProviderJson.MaxValueNodes)) + "]");

        StorageProviderJson.Canonicalize(allowedDepth, "NonSecretConfig").ShouldNotBeNullOrEmpty();
        Should.Throw<ArgumentException>(() => StorageProviderJson.Canonicalize(excessiveDepth, "NonSecretConfig")).Message.ShouldContain("depth");
        Should.Throw<ArgumentException>(() => StorageProviderJson.Canonicalize(excessiveNodes, "NonSecretConfig")).Message.ShouldContain("nodes");
    }

    private static void ShouldReject(JsonElement schema, string json, string message)
    {
        var error = Should.Throw<ArgumentException>(() => StorageProviderJson.Validate(Json(json), schema, "Secret"));
        error.Message.ShouldContain(message, Case.Insensitive);
        error.Message.ShouldNotContain("raw-secret");
    }

    private static JsonElement Json(string value)
    {
        using var document = JsonDocument.Parse(value);
        return document.RootElement.Clone();
    }
}
