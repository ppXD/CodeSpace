using System.Text.Json;
using CodeSpace.Core.Services.Workflows.Runtime;
using Shouldly;

namespace CodeSpace.UnitTests.Workflows;

/// <summary>
/// Pins the redaction contract for engine persistence points. Each test expresses ONE
/// invariant the engine (and any future caller) relies on. Adding a new persistence point
/// means picking ONE of these guarantees as your assertion.
/// </summary>
[Trait("Category", "Unit")]
public class PayloadRedactorTests
{
    private readonly IPayloadRedactor _redactor = new PayloadRedactor();

    private static JsonElement J(string raw) => JsonDocument.Parse(raw).RootElement.Clone();

    // ─── Pass-through cases ─────────────────────────────────────────────────────

    [Fact]
    public void Empty_secret_set_passes_through_unchanged()
    {
        // Fast path: no secrets in scope → no redaction work.
        var template = J("""{"a":"{{trigger.title}}","b":"{{wf.X}}"}""");
        var resolved = new Dictionary<string, JsonElement>
        {
            ["a"] = J("\"PR title\""),
            ["b"] = J("\"plain value\""),
        };

        var result = _redactor.RedactBag(template, resolved, new HashSet<string>());

        result["a"].GetString().ShouldBe("PR title");
        result["b"].GetString().ShouldBe("plain value");
    }

    [Fact]
    public void Bag_with_only_non_secret_refs_passes_through_unchanged()
    {
        // Secret set is non-empty but none of the bag's templates reference any of them.
        var template = J("""{"a":"{{trigger.title}}","b":"{{wf.NON_SECRET}}"}""");
        var resolved = new Dictionary<string, JsonElement>
        {
            ["a"] = J("\"PR title\""),
            ["b"] = J("\"plain wf value\""),
        };
        var secretPaths = new HashSet<string> { "team.API_KEY", "wf.PASSWORD" };

        var result = _redactor.RedactBag(template, resolved, secretPaths);

        result["a"].GetString().ShouldBe("PR title");
        result["b"].GetString().ShouldBe("plain wf value");
    }

    // ─── Redaction cases ────────────────────────────────────────────────────────

    [Fact]
    public void Sole_template_referencing_a_secret_path_redacts_the_whole_value()
    {
        // Canonical case: {{team.SECRET}} as the entire value of a bag key.
        var template = J("""{"key":"{{team.API_KEY}}","title":"{{trigger.title}}"}""");
        var resolved = new Dictionary<string, JsonElement>
        {
            ["key"]   = J("\"sk-very-sensitive-12345\""),
            ["title"] = J("\"My PR\""),
        };
        var secretPaths = new HashSet<string> { "team.API_KEY" };

        var result = _redactor.RedactBag(template, resolved, secretPaths);

        result["key"].GetString().ShouldBe("[REDACTED: team.API_KEY]",
            "the secret key's whole value MUST be replaced with the marker preserving the path name");
        result["title"].GetString().ShouldBe("My PR",
            "the other key MUST be unchanged");
    }

    [Fact]
    public void Mixed_string_with_embedded_secret_template_redacts_the_whole_value()
    {
        // Common real case: "Bearer {{team.API_KEY}}" as an HTTP Authorization header value.
        // We replace the WHOLE string (not just the {{...}} part) because partial replacement
        // is fragile and the operator's expectation is "this field is tainted, hide it".
        var template = J("""{"Authorization":"Bearer {{team.API_KEY}}"}""");
        var resolved = new Dictionary<string, JsonElement>
        {
            ["Authorization"] = J("\"Bearer sk-real-secret-xyz\""),
        };
        var secretPaths = new HashSet<string> { "team.API_KEY" };

        var result = _redactor.RedactBag(template, resolved, secretPaths);

        result["Authorization"].GetString().ShouldBe("[REDACTED: team.API_KEY]",
            "mixed-content strings with a secret template MUST be fully redacted — character-level cutting is fragile");
        result["Authorization"].GetString()!.Contains("sk-real-secret-xyz").ShouldBeFalse(
            "the plaintext secret MUST NOT survive in the redacted output");
    }

    [Fact]
    public void Dollar_ref_to_a_secret_path_redacts_the_value()
    {
        // {"$ref": "team.SECRET"} is the structured-reference form (returns the whole value
        // not a string). It still references the secret path so it must be redacted.
        var template = J("""{"creds":{"$ref":"team.API_KEY"}}""");
        var resolved = new Dictionary<string, JsonElement>
        {
            ["creds"] = J("\"sk-via-dollar-ref\""),
        };
        var secretPaths = new HashSet<string> { "team.API_KEY" };

        var result = _redactor.RedactBag(template, resolved, secretPaths);

        result["creds"].GetString().ShouldBe("[REDACTED: team.API_KEY]");
    }

    [Fact]
    public void Nested_object_with_secret_ref_redacts_only_the_tainted_leaf_inside()
    {
        // Critical UX guarantee: when a nested object has ONE tainted child and others clean,
        // we descend into the object and redact only the tainted leaf. Sibling keys pass
        // through. This preserves debugging value (operator still sees Content-Type, etc.)
        // while protecting the secret-tainted Authorization header.
        var template = J("""{"options":{"timeout":30,"auth":"{{team.API_KEY}}"}}""");
        var resolved = new Dictionary<string, JsonElement>
        {
            ["options"] = J("""{"timeout":30,"auth":"sk-resolved-secret"}"""),
        };
        var secretPaths = new HashSet<string> { "team.API_KEY" };

        var result = _redactor.RedactBag(template, resolved, secretPaths);

        result["options"].ValueKind.ShouldBe(JsonValueKind.Object,
            "redactor MUST descend into nested objects, preserving structure for untainted siblings");
        result["options"].GetProperty("timeout").GetInt32().ShouldBe(30,
            "untainted sibling MUST pass through unchanged");
        result["options"].GetProperty("auth").GetString().ShouldBe("[REDACTED: team.API_KEY]",
            "only the tainted leaf inside the nested object is replaced");
    }

    [Fact]
    public void Deeply_nested_secret_redacts_at_smallest_containing_key()
    {
        // Real-world case: HTTP request inputs have headers as a nested object. We MUST
        // redact only the Authorization header, not the whole headers object — otherwise
        // operators can't debug Content-Type / User-Agent issues.
        var template = J("""
            {
                "url": "https://api.example.com",
                "method": "GET",
                "headers": {
                    "Authorization": "Bearer {{team.API_KEY}}",
                    "Content-Type": "application/json",
                    "User-Agent": "{{wf.UA}}"
                }
            }
            """);
        var resolved = new Dictionary<string, JsonElement>
        {
            ["url"]     = J("\"https://api.example.com\""),
            ["method"]  = J("\"GET\""),
            ["headers"] = J("""
                {
                    "Authorization": "Bearer sk-real-secret",
                    "Content-Type":  "application/json",
                    "User-Agent":    "MyApp/1.0"
                }
                """),
        };
        var secretPaths = new HashSet<string> { "team.API_KEY" };

        var result = _redactor.RedactBag(template, resolved, secretPaths);

        var headers = result["headers"];
        headers.ValueKind.ShouldBe(JsonValueKind.Object);
        headers.GetProperty("Authorization").GetString().ShouldBe("[REDACTED: team.API_KEY]");
        headers.GetProperty("Content-Type").GetString().ShouldBe("application/json",
            "non-secret header MUST be visible for debugging");
        headers.GetProperty("User-Agent").GetString().ShouldBe("MyApp/1.0",
            "non-secret wf.* variable resolution MUST pass through");
    }

    [Fact]
    public void Multiple_secrets_in_one_template_value_redacts_with_first_path_in_marker()
    {
        // If both {{team.A}} and {{team.B}} are referenced AND both are secrets, the marker
        // names ONE of them (impl-detail which — order of refs in the template). The point
        // is full redaction; the marker is debugging-only.
        var template = J("""{"combined":"{{team.A}} and {{team.B}}"}""");
        var resolved = new Dictionary<string, JsonElement>
        {
            ["combined"] = J("\"alpha and beta\""),
        };
        var secretPaths = new HashSet<string> { "team.A", "team.B" };

        var result = _redactor.RedactBag(template, resolved, secretPaths);

        var redacted = result["combined"].GetString()!;
        redacted.ShouldStartWith("[REDACTED:");
        // Don't pin which path appears in the marker — just that one of them does + the value is gone.
        (redacted.Contains("team.A") || redacted.Contains("team.B")).ShouldBeTrue();
        redacted.Contains("alpha").ShouldBeFalse();
        redacted.Contains("beta").ShouldBeFalse();
    }

    // ─── Edge cases ────────────────────────────────────────────────────────────

    [Fact]
    public void Template_missing_key_for_resolved_value_passes_resolved_through_unchanged()
    {
        // Engine sometimes adds synthetic keys to the resolved bag that aren't in the template
        // (defaults, fallback values). No template → no taint info → conservative pass-through.
        var template = J("""{"a":"{{trigger.x}}"}""");
        var resolved = new Dictionary<string, JsonElement>
        {
            ["a"] = J("\"value-a\""),
            ["synthetic"] = J("\"engine-injected\""),
        };
        var secretPaths = new HashSet<string> { "team.SECRET" };

        var result = _redactor.RedactBag(template, resolved, secretPaths);

        result.Keys.OrderBy(k => k).ShouldBe(new[] { "a", "synthetic" });
        result["synthetic"].GetString().ShouldBe("engine-injected");
    }

    [Fact]
    public void Non_object_template_returns_resolved_bag_unchanged()
    {
        // Defensive: if caller passes a malformed template (not an object) we don't crash,
        // just return the bag as-is. Engine should never do this, but the API stays robust.
        var template = J("\"this is not an object\"");
        var resolved = new Dictionary<string, JsonElement>
        {
            ["a"] = J("\"value-a\""),
        };
        var secretPaths = new HashSet<string> { "team.SECRET" };

        var result = _redactor.RedactBag(template, resolved, secretPaths);

        result["a"].GetString().ShouldBe("value-a");
    }

    [Fact]
    public void Marker_format_is_pinned_for_consumer_string_matching()
    {
        // Frontend / log scanners may search for the marker prefix to flag redacted values
        // visually. Pin the exact format so any future re-format is a compile-error-visible
        // decision (a UI string that scans for "[REDACTED:" stops working otherwise).
        var template = J("""{"a":"{{team.X}}"}""");
        var resolved = new Dictionary<string, JsonElement> { ["a"] = J("\"v\"") };

        var result = _redactor.RedactBag(template, resolved, new HashSet<string> { "team.X" });

        var redacted = result["a"].GetString()!;
        redacted.ShouldStartWith("[REDACTED: ");
        redacted.ShouldEndWith("]");
        redacted.ShouldContain("team.X");
    }

    [Fact]
    public void Resolved_value_can_be_any_JSON_kind_redaction_replaces_with_string_marker()
    {
        // The resolved value at a redaction site might be a number, boolean, object, array.
        // After redaction it becomes a String (the marker). This is intentional — the
        // ledger consumer can detect "this used to be a typed value but got redacted" by
        // looking for the marker prefix.
        var template = J("""{"port":"{{team.PORT}}","enabled":"{{team.FLAG}}"}""");
        var resolved = new Dictionary<string, JsonElement>
        {
            ["port"]    = J("443"),
            ["enabled"] = J("true"),
        };
        var secretPaths = new HashSet<string> { "team.PORT", "team.FLAG" };

        var result = _redactor.RedactBag(template, resolved, secretPaths);

        result["port"].ValueKind.ShouldBe(JsonValueKind.String);
        result["port"].GetString().ShouldStartWith("[REDACTED:");
        result["enabled"].ValueKind.ShouldBe(JsonValueKind.String);
        result["enabled"].GetString().ShouldStartWith("[REDACTED:");
    }

    // ─── No-false-positive guarantee ─────────────────────────────────────────

    [Fact]
    public void Plaintext_matching_secret_value_but_not_referenced_via_template_is_NOT_redacted()
    {
        // CRITICAL: the redactor MUST be template-driven, not value-scan driven. A user
        // who happens to type the literal string "sk-real-secret" in an input field
        // (because it's a documentation example, etc) MUST NOT be silently redacted.
        // Only values whose template path referenced a secret get redacted.
        var template = J("""{"docs":"Set your key like sk-real-secret-example"}""");
        var resolved = new Dictionary<string, JsonElement>
        {
            ["docs"] = J("\"Set your key like sk-real-secret-example\""),
        };
        var secretPaths = new HashSet<string> { "team.API_KEY" };

        var result = _redactor.RedactBag(template, resolved, secretPaths);

        result["docs"].GetString().ShouldBe("Set your key like sk-real-secret-example",
            "string content matching a secret VALUE by coincidence MUST NOT trigger redaction — only template-referenced paths matter");
    }
}
