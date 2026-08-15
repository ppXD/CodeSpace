using System.Text.Json;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Workflows.Artifacts.Profiles;
using Shouldly;

namespace CodeSpace.UnitTests.Workflows.Artifacts.Profiles;

public sealed class StorageProfileRulesTests
{
    [Fact]
    public void Config_validation_is_deterministic_and_rejects_missing_wrong_typed_extra_and_secret_fields()
    {
        var configSchema = Json("""
            {
              "type": "object",
              "properties": {
                "bucket": { "type": "string" },
                "replicas": { "type": "integer" },
                "options": {
                  "type": "object",
                  "properties": { "compressed": { "type": "boolean" } },
                  "required": ["compressed"],
                  "additionalProperties": false
                }
              },
              "required": ["bucket", "options"],
              "additionalProperties": false
            }
            """);
        var secretSchema = Json("""{ "type": "object", "properties": { "accessKeySecret": { "type": "string" } }, "additionalProperties": false }""");

        StorageProfileRules.ValidateConfig(Json("""{ "bucket": "builds", "replicas": 2, "options": { "compressed": true } }"""), configSchema, secretSchema);

        StorageProfileRuleTestExtensions.ShouldFailWith(() => StorageProfileRules.ValidateConfig(Json("""[]"""), configSchema, secretSchema), "must be a JSON object");
        StorageProfileRuleTestExtensions.ShouldFailWith(() => StorageProfileRules.ValidateConfig(Json("""{ "bucket": "builds" }"""), configSchema, secretSchema), "options");
        StorageProfileRuleTestExtensions.ShouldFailWith(() => StorageProfileRules.ValidateConfig(Json("""{ "bucket": "builds", "replicas": 1.5, "options": { "compressed": true } }"""), configSchema, secretSchema), "integer");
        StorageProfileRuleTestExtensions.ShouldFailWith(() => StorageProfileRules.ValidateConfig(Json("""{ "bucket": "builds", "unknown": true, "options": { "compressed": true } }"""), configSchema, secretSchema), "unknown");

        var openConfig = Json("""{ "type": "object", "properties": { "bucket": { "type": "string" } }, "additionalProperties": true }""");
        StorageProfileRuleTestExtensions.ShouldFailWith(() => StorageProfileRules.ValidateConfig(Json("""{ "bucket": "builds", "accessKeySecret": "must-not-cross" }"""), openConfig, secretSchema), "secret input");
    }

    [Fact]
    public void Secret_schema_rejection_follows_nested_object_properties()
    {
        var configSchema = Json("""
            {
              "type": "object",
              "properties": {
                "auth": { "type": "object", "additionalProperties": true }
              },
              "additionalProperties": false
            }
            """);
        var secretSchema = Json("""
            {
              "type": "object",
              "properties": {
                "auth": {
                  "type": "object",
                  "properties": { "accessKeySecret": { "type": "string" } }
                }
              }
            }
            """);

        StorageProfileRules.ValidateConfig(Json("""{ "auth": { "region": "cn-hangzhou" } }"""), configSchema, secretSchema);
        StorageProfileRuleTestExtensions.ShouldFailWith(
            () => StorageProfileRules.ValidateConfig(Json("""{ "auth": { "region": "cn-hangzhou", "accessKeySecret": "must-not-cross" } }"""), configSchema, secretSchema),
            "auth.accessKeySecret");
    }

    [Fact]
    public void Supported_assertions_are_enforced_and_unknown_assertion_keywords_fail_closed()
    {
        var schema = Json("""
            {
              "type": "object",
              "properties": {
                "bucket": { "type": "string", "pattern": "^[a-z]+$", "minLength": 3, "maxLength": 8 },
                "replicas": { "type": "integer", "minimum": 1, "maximum": 3 },
                "regions": { "type": "array", "minItems": 1, "maxItems": 2, "items": { "enum": ["cn", "us"] } },
                "mode": { "const": "durable" }
              },
              "additionalProperties": false
            }
            """);
        var noSecrets = Json("""{ "type": "object", "properties": {} }""");

        StorageProfileRules.ValidateConfig(Json("""{ "bucket": "builds", "replicas": 2, "regions": ["cn"], "mode": "durable" }"""), schema, noSecrets);
        StorageProfileRuleTestExtensions.ShouldFailWith(() => StorageProfileRules.ValidateConfig(Json("""{ "bucket": "B", "replicas": 4, "regions": [], "mode": "fast" }"""), schema, noSecrets), "bucket");

        var unsupported = Json("""{ "type": "object", "oneOf": [{ "required": ["bucket"] }] }""");
        StorageProfileRuleTestExtensions.ShouldFailWith(() => StorageProfileRules.ValidateConfig(Json("""{}"""), unsupported, noSecrets), "oneOf");
    }

    [Fact]
    public void Canonical_config_and_namespace_fingerprint_are_order_independent_and_server_derived()
    {
        var first = Json("""{ "prefix": "runs", "bucket": "builds", "nested": { "z": 2, "a": 1 } }""");
        var second = Json("""{ "nested": { "a": 1, "z": 2 }, "bucket": "builds", "prefix": "runs" }""");

        var firstCanonical = StorageProfileRules.CanonicalJson(first);
        var secondCanonical = StorageProfileRules.CanonicalJson(second);

        firstCanonical.ShouldBe(secondCanonical);
        StorageProfileRules.NamespaceFingerprint("object-store/v1", first).ShouldBe(StorageProfileRules.NamespaceFingerprint("object-store/v1", second));
        StorageProfileRules.NamespaceFingerprint("object-store/v1", first).ShouldStartWith("sha256:");
        StorageProfileRules.NamespaceFingerprint("object-store/v1", first).Length.ShouldBe(71);
    }

    [Theory]
    [InlineData("db:11111111-2222-3333-4444-555555555555:7", "db:11111111-2222-3333-4444-555555555555:7")]
    [InlineData("db:11111111222233334444555555555555:7", null)]
    [InlineData("env:ACCESS_KEY", null)]
    [InlineData("actual-secret-value", null)]
    [InlineData("db:11111111-2222-3333-4444-555555555555:0", null)]
    public void Credential_refs_accept_only_db_uuid_positive_version(string value, string? expected)
    {
        var parsed = StorageProfileRules.TryParseCredentialRef(value, out var reference);

        parsed.ShouldBe(expected != null);
        if (expected != null) reference.Canonical.ShouldBe(expected);
    }

    [Fact]
    public void Lifecycle_allows_managed_states_but_retired_is_terminal()
    {
        StorageProfileRules.EnsureTransition(StorageProfileState.Draft, StorageProfileState.Active);
        StorageProfileRules.EnsureTransition(StorageProfileState.Active, StorageProfileState.Disabled);
        StorageProfileRules.EnsureTransition(StorageProfileState.Disabled, StorageProfileState.Active);
        StorageProfileRules.EnsureTransition(StorageProfileState.Active, StorageProfileState.Retired);

        Should.Throw<ArgumentException>(() => StorageProfileRules.EnsureTransition(StorageProfileState.Active, StorageProfileState.Draft)).Message.ShouldContain("Draft");
        Should.Throw<ArgumentException>(() => StorageProfileRules.EnsureTransition(StorageProfileState.Retired, StorageProfileState.Active)).Message.ShouldContain("terminal");
        Should.Throw<ArgumentException>(() => StorageProfileRules.EnsureRevisionAllowed(StorageProfileState.Retired)).Message.ShouldContain("terminal");
    }

    private static JsonElement Json(string value)
    {
        using var document = JsonDocument.Parse(value);
        return document.RootElement.Clone();
    }
}

file static class StorageProfileRuleTestExtensions
{
    public static void ShouldFailWith(Action action, string text) => Should.Throw<ArgumentException>(action).Message.ShouldContain(text, Case.Insensitive);
}
