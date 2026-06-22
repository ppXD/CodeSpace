using System.Text.Json;

namespace CodeSpace.Core.Services.Workflows.Llm;

/// <summary>
/// A FOCUSED, dependency-free JSON-Schema check for structured LLM output — it validates the high-value keywords that
/// catch the real model-output failures (a MISSING required field, a WRONG-shaped value, an INVALID enum), recursively
/// through <c>properties</c> + array <c>items</c>. It is deliberately LENIENT on the rest (additionalProperties, string
/// formats, numeric bounds): a model commonly adds a harmless extra field, and the goal is to reject garbage
/// (<c>{}</c> against a schema that requires <c>kind</c>) and steer a re-ask — NOT to be a full conformance oracle.
/// Returns the human-readable violations (empty ⇒ valid) so a re-ask prompt can name exactly what to fix.
/// </summary>
internal static class JsonSchemaValidator
{
    private const int MaxErrors = 12;   // a long error list helps nobody; cap so the re-ask prompt stays focused

    public static IReadOnlyList<string> Validate(JsonElement instance, JsonElement schema)
    {
        var errors = new List<string>();
        ValidateNode(instance, schema, "$", errors);
        return errors;
    }

    private static void ValidateNode(JsonElement instance, JsonElement schema, string path, List<string> errors)
    {
        if (errors.Count >= MaxErrors || schema.ValueKind != JsonValueKind.Object) return;

        if (schema.TryGetProperty("enum", out var enumValues) && enumValues.ValueKind == JsonValueKind.Array)
            ValidateEnum(instance, enumValues, path, errors);

        if (schema.TryGetProperty("type", out var typeEl) && typeEl.ValueKind == JsonValueKind.String)
        {
            if (!TypeMatches(instance, typeEl.GetString()!))
            {
                errors.Add($"{path}: expected type '{typeEl.GetString()}' but got {KindName(instance.ValueKind)}");
                return;   // a type mismatch makes the sub-checks (required/items) meaningless
            }
        }

        if (instance.ValueKind == JsonValueKind.Object) ValidateObject(instance, schema, path, errors);
        else if (instance.ValueKind == JsonValueKind.Array) ValidateArray(instance, schema, path, errors);
    }

    private static void ValidateObject(JsonElement instance, JsonElement schema, string path, List<string> errors)
    {
        if (schema.TryGetProperty("required", out var required) && required.ValueKind == JsonValueKind.Array)
            foreach (var req in required.EnumerateArray())
            {
                if (errors.Count >= MaxErrors) return;
                if (req.ValueKind == JsonValueKind.String && !instance.TryGetProperty(req.GetString()!, out _))
                    errors.Add($"{path}: missing required property '{req.GetString()}'");
            }

        if (schema.TryGetProperty("properties", out var props) && props.ValueKind == JsonValueKind.Object)
            foreach (var prop in props.EnumerateObject())
            {
                if (errors.Count >= MaxErrors) return;
                if (instance.TryGetProperty(prop.Name, out var child))
                    ValidateNode(child, prop.Value, $"{path}.{prop.Name}", errors);   // recurse only into PRESENT props
            }
    }

    private static void ValidateArray(JsonElement instance, JsonElement schema, string path, List<string> errors)
    {
        if (!schema.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Object) return;

        var i = 0;
        foreach (var element in instance.EnumerateArray())
        {
            if (errors.Count >= MaxErrors) return;
            ValidateNode(element, items, $"{path}[{i++}]", errors);
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
        "integer" => instance.ValueKind == JsonValueKind.Number && instance.TryGetInt64(out _),
        "number" => instance.ValueKind == JsonValueKind.Number,
        "null" => instance.ValueKind == JsonValueKind.Null,
        _ => true,   // an unknown/compound type (e.g. a "type": ["string","null"] array we don't parse) → don't reject
    };

    private static bool JsonValueEquals(JsonElement a, JsonElement b)
    {
        if (a.ValueKind != b.ValueKind) return false;

        return a.ValueKind switch
        {
            JsonValueKind.String => a.GetString() == b.GetString(),
            JsonValueKind.Number => a.GetRawText() == b.GetRawText(),
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
