using System.Text.Json;

namespace CodeSpace.Core.Services.Workflows.Llm;

/// <summary>
/// A FOCUSED, dependency-free JSON-Schema check for structured LLM output — it validates the high-value keywords that
/// catch the real model-output failures (a MISSING required field, a WRONG-shaped value, an INVALID enum), recursively
/// through <c>properties</c>, bounded arrays and alternative schemas. It is deliberately LENIENT on the rest (additionalProperties, string
/// formats, numeric bounds): a model commonly adds a harmless extra field, and the goal is to reject garbage
/// (<c>{}</c> against a schema that requires <c>kind</c>) and steer a re-ask — NOT to be a full conformance oracle.
/// Returns the human-readable violations (empty ⇒ valid) so a re-ask prompt can name exactly what to fix.
/// </summary>
internal static class JsonSchemaValidator
{
    private const int MaxErrors = 12;   // a long error list helps nobody; cap so the re-ask prompt stays focused
    private sealed class ValidationBudget { public int Remaining = 4096; }

    public static IReadOnlyList<string> Validate(JsonElement instance, JsonElement schema)
    {
        var errors = new List<string>();
        var budget = new ValidationBudget();
        ValidateNode(instance, schema, "$", errors, budget);
        return budget.Remaining < 0 ? ["$: schema validation work budget exceeded; simplify the structured response or schema."] : errors;
    }

    private static void ValidateNode(JsonElement instance, JsonElement schema, string path, List<string> errors, ValidationBudget budget)
    {
        if (errors.Count >= MaxErrors || --budget.Remaining < 0) return;
        if (schema.ValueKind == JsonValueKind.False) { errors.Add($"{path}: this value is forbidden by its schema"); return; }
        if (schema.ValueKind != JsonValueKind.Object) return;

        if (schema.TryGetProperty("enum", out var enumValues) && enumValues.ValueKind == JsonValueKind.Array)
            ValidateEnum(instance, enumValues, path, errors);

        if (schema.TryGetProperty("type", out var typeEl) && typeEl.ValueKind is JsonValueKind.String or JsonValueKind.Array)
        {
            var matches = typeEl.ValueKind == JsonValueKind.String ? TypeMatches(instance, typeEl.GetString()!) : typeEl.EnumerateArray().Any(type => type.ValueKind == JsonValueKind.String && TypeMatches(instance, type.GetString()!));
            if (!matches)
            {
                errors.Add($"{path}: expected type '{typeEl}' but got {KindName(instance.ValueKind)}");
                return;   // a type mismatch makes the sub-checks (required/items) meaningless
            }
        }

        ValidateAlternatives(instance, schema, path, errors, budget);
        if (schema.TryGetProperty("not", out var forbidden))
        {
            var violations = new List<string>();
            ValidateNode(instance, forbidden, path, violations, budget);
            if (violations.Count == 0 && errors.Count < MaxErrors) errors.Add($"{path}: matches a forbidden schema");
        }
        if (instance.ValueKind == JsonValueKind.Object) ValidateObject(instance, schema, path, errors, budget);
        else if (instance.ValueKind == JsonValueKind.Array) ValidateArray(instance, schema, path, errors, budget);
    }

    private static void ValidateAlternatives(JsonElement instance, JsonElement schema, string path, List<string> errors, ValidationBudget budget)
    {
        foreach (var keyword in new[] { "oneOf", "anyOf" })
        {
            if (!schema.TryGetProperty(keyword, out var alternatives) || alternatives.ValueKind != JsonValueKind.Array) continue;
            var matches = 0;
            List<string>? closest = null;
            foreach (var alternative in alternatives.EnumerateArray())
            {
                if (budget.Remaining < 0) return;
                var violations = new List<string>();
                ValidateNode(instance, alternative, path, violations, budget);
                if (violations.Count == 0) matches++;
                else if (closest is null || violations.Count < closest.Count) closest = violations;
            }
            if (matches == 1 || keyword == "anyOf" && matches > 0) continue;
            if (errors.Count < MaxErrors) errors.Add($"{path}: {keyword} requires {(keyword == "oneOf" ? "exactly one" : "at least one")} matching schema; matched {matches}");
            if (matches == 0 && closest is not null) errors.AddRange(closest.Take(Math.Max(0, MaxErrors - errors.Count)));
        }
    }

    private static void ValidateObject(JsonElement instance, JsonElement schema, string path, List<string> errors, ValidationBudget budget)
    {
        if (schema.TryGetProperty("required", out var required) && required.ValueKind == JsonValueKind.Array)
            foreach (var req in required.EnumerateArray())
            {
                if (errors.Count >= MaxErrors || budget.Remaining < 0) return;
                if (req.ValueKind == JsonValueKind.String && !instance.TryGetProperty(req.GetString()!, out _))
                    errors.Add($"{path}: missing required property '{req.GetString()}'");
            }

        if (schema.TryGetProperty("properties", out var props) && props.ValueKind == JsonValueKind.Object)
            foreach (var prop in props.EnumerateObject())
            {
                if (errors.Count >= MaxErrors || budget.Remaining < 0) return;
                if (instance.TryGetProperty(prop.Name, out var child))
                    ValidateNode(child, prop.Value, $"{path}.{prop.Name}", errors, budget);   // recurse only into PRESENT props
            }
    }

    private static void ValidateArray(JsonElement instance, JsonElement schema, string path, List<string> errors, ValidationBudget budget)
    {
        if (errors.Count < MaxErrors && schema.TryGetProperty("minItems", out var minimum) && minimum.ValueKind == JsonValueKind.Number && minimum.TryGetInt32(out var min) && instance.GetArrayLength() < min) errors.Add($"{path}: requires at least {min} items");
        if (errors.Count < MaxErrors && schema.TryGetProperty("maxItems", out var maximum) && maximum.ValueKind == JsonValueKind.Number && maximum.TryGetInt32(out var max) && instance.GetArrayLength() > max) errors.Add($"{path}: allows at most {max} items");
        if (!schema.TryGetProperty("items", out var items)) return;

        var i = 0;
        foreach (var element in instance.EnumerateArray())
        {
            if (errors.Count >= MaxErrors || budget.Remaining < 0) return;
            ValidateNode(element, items, $"{path}[{i++}]", errors, budget);
        }
    }

    private static void ValidateEnum(JsonElement instance, JsonElement enumValues, string path, List<string> errors)
    {
        foreach (var allowed in enumValues.EnumerateArray())
            if (JsonValueEquals(instance, allowed)) return;

        var options = string.Join(", ", enumValues.EnumerateArray().Select(e => e.ToString()));
        errors.Add($"{path}: value '{instance}' is not one of the allowed enum values [{options}]");
    }

    private static bool TypeMatches(JsonElement instance, string type) => type switch
    {
        "object" => instance.ValueKind == JsonValueKind.Object,
        "array" => instance.ValueKind == JsonValueKind.Array,
        "string" => instance.ValueKind == JsonValueKind.String,
        "boolean" => instance.ValueKind is JsonValueKind.True or JsonValueKind.False,
        // JSON Schema treats a number with no fractional part as an integer — so 5.0 IS a valid integer (TryGetInt64
        // alone would wrongly reject it). Accept any Number whose value truncates to itself.
        "integer" => instance.ValueKind == JsonValueKind.Number && instance.TryGetDouble(out var d) && d == Math.Truncate(d),
        "number" => instance.ValueKind == JsonValueKind.Number,
        "null" => instance.ValueKind == JsonValueKind.Null,
        _ => true,   // unknown keywords remain outside this focused validator's supported subset
    };

    private static bool JsonValueEquals(JsonElement a, JsonElement b)
    {
        if (a.ValueKind != b.ValueKind) return false;

        return a.ValueKind switch
        {
            JsonValueKind.String => a.GetString() == b.GetString(),
            // Compare numbers by VALUE so an enum of 1 matches a model's 1.0 (GetRawText would mismatch the spelling).
            JsonValueKind.Number => a.TryGetDouble(out var av) && b.TryGetDouble(out var bv) && av == bv,
            JsonValueKind.True or JsonValueKind.False or JsonValueKind.Null => true,
            _ => a.GetRawText() == b.GetRawText(),
        };
    }

    private static string KindName(JsonValueKind kind) => kind switch
    {
        JsonValueKind.True or JsonValueKind.False => "boolean",
        _ => kind.ToString().ToLowerInvariant(),
    };
}
