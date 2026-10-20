using System.Text.Json;
using System.Numerics;

namespace CodeSpace.Core.Services.Workflows.ExternalTriggers;

/// <summary>
/// Validates external request bodies strictly against the supported input-schema keywords. The LLM validator is
/// deliberately lenient about additional properties, so using it here would silently weaken a public API contract.
/// </summary>
public static class ExternalTriggerPayloadValidator
{
    public static IReadOnlyList<string> Validate(JsonElement body, JsonElement inputSchema)
    {
        var errors = new List<string>();
        ValidateNode(body, inputSchema, "$", errors);
        return errors;
    }

    private static void ValidateNode(JsonElement value, JsonElement schema, string path, List<string> errors)
    {
        if (schema.ValueKind != JsonValueKind.Object) return;

        if (schema.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.String &&
            !TypeMatches(value, type.GetString()!))
        {
            errors.Add($"{path}: expected type '{type.GetString()}' but got {KindName(value.ValueKind)}");
            return;
        }

        if (schema.TryGetProperty("enum", out var enumValues) && enumValues.ValueKind == JsonValueKind.Array &&
            !enumValues.EnumerateArray().Any(candidate => JsonValueEquals(value, candidate)))
            errors.Add($"{path}: value '{value}' is not one of the allowed enum values");

        if (value.ValueKind == JsonValueKind.Object)
        {
            ValidateObject(value, schema, path, errors);
        }
        else if (value.ValueKind == JsonValueKind.Array && schema.TryGetProperty("items", out var items))
        {
            var index = 0;
            foreach (var item in value.EnumerateArray())
                ValidateNode(item, items, $"{path}[{index++}]", errors);
        }
        else if (value.ValueKind == JsonValueKind.String && schema.TryGetProperty("maxLength", out var maxLength) &&
                 maxLength.TryGetInt32(out var maximum) && value.GetString()!.Length > maximum)
        {
            errors.Add($"{path}: string exceeds maximum length {maximum}");
        }
    }

    private static void ValidateObject(JsonElement value, JsonElement schema, string path, List<string> errors)
    {
        if (schema.TryGetProperty("required", out var required) && required.ValueKind == JsonValueKind.Array)
        {
            foreach (var property in required.EnumerateArray())
            {
                if (property.ValueKind == JsonValueKind.String && !value.TryGetProperty(property.GetString()!, out _))
                    errors.Add($"{path}.{property.GetString()}: required property is missing");
            }
        }

        var hasProperties = schema.TryGetProperty("properties", out var properties) && properties.ValueKind == JsonValueKind.Object;
        var closesObject = schema.TryGetProperty("additionalProperties", out var additionalProperties) &&
                           additionalProperties.ValueKind == JsonValueKind.False;

        foreach (var property in value.EnumerateObject())
        {
            if (hasProperties && properties.TryGetProperty(property.Name, out var propertySchema))
                ValidateNode(property.Value, propertySchema, $"{path}.{property.Name}", errors);
            else if (closesObject)
                errors.Add($"{path}.{property.Name}: additional property is not allowed");
        }
    }

    private static bool TypeMatches(JsonElement value, string type) => type switch
    {
        "object" => value.ValueKind == JsonValueKind.Object,
        "array" => value.ValueKind == JsonValueKind.Array,
        "string" => value.ValueKind == JsonValueKind.String,
        "integer" => value.ValueKind == JsonValueKind.Number && IsInteger(value.GetRawText()),
        "number" => value.ValueKind == JsonValueKind.Number,
        "boolean" => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
        "null" => value.ValueKind == JsonValueKind.Null,
        _ => true,
    };

    private static bool IsInteger(string rawNumber)
    {
        var exponentIndex = rawNumber.IndexOfAny(['e', 'E']);
        var mantissa = exponentIndex < 0 ? rawNumber : rawNumber[..exponentIndex];
        var exponent = exponentIndex < 0 ? BigInteger.Zero : BigInteger.Parse(rawNumber[(exponentIndex + 1)..], System.Globalization.CultureInfo.InvariantCulture);
        var decimalIndex = mantissa.IndexOf('.');
        var fractionalDigits = decimalIndex < 0 ? 0 : mantissa.Length - decimalIndex - 1;
        var digits = mantissa.Replace(".", "", StringComparison.Ordinal).TrimStart('-');
        var significantDigits = digits.TrimEnd('0');
        if (significantDigits.Length == 0) return true;

        // JSON Schema treats 5.0 as an integer: accept a decimal token when its exact value has no fractional
        // part after applying the exponent. Checking token digits avoids double rounding away a non-zero fraction.
        var remainingFractionalDigits = new BigInteger(fractionalDigits) - exponent;
        if (remainingFractionalDigits <= BigInteger.Zero) return true;
        return remainingFractionalDigits <= digits.Length - significantDigits.Length;
    }

    private static bool JsonValueEquals(JsonElement left, JsonElement right)
    {
        if (left.ValueKind != right.ValueKind) return false;

        return left.ValueKind switch
        {
            JsonValueKind.String => left.GetString() == right.GetString(),
            JsonValueKind.Number => left.TryGetDouble(out var leftNumber) && right.TryGetDouble(out var rightNumber) && leftNumber == rightNumber,
            JsonValueKind.True or JsonValueKind.False or JsonValueKind.Null => true,
            _ => left.GetRawText() == right.GetRawText(),
        };
    }

    private static string KindName(JsonValueKind kind) => kind switch
    {
        JsonValueKind.True or JsonValueKind.False => "boolean",
        _ => kind.ToString().ToLowerInvariant(),
    };
}
