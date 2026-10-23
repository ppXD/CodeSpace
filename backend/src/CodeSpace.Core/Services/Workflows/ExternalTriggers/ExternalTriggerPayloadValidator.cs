using System.Text.Json;
using System.Numerics;
using System.Globalization;

namespace CodeSpace.Core.Services.Workflows.ExternalTriggers;

/// <summary>
/// Validates external request bodies strictly against the supported input-schema keywords. The LLM validator is
/// deliberately lenient about additional properties, so using it here would silently weaken a public API contract.
/// </summary>
public static class ExternalTriggerPayloadValidator
{
    public const int MaxErrors = 20;
    public const int MaxNumberTokenLength = 256;
    public const int MaxSchemaDepth = 64;
    public const int MaxNodes = 10_000;

    /// <summary>
    /// The schema keywords the HTTP contract actually enforces. A keyword outside this set is refused at save time
    /// rather than silently weakening the published contract.
    /// </summary>
    private static readonly HashSet<string> SupportedSchemaKeywords = new(StringComparer.Ordinal)
    {
        "type", "enum", "required", "properties", "additionalProperties", "items", "maxLength", "title", "description", "default",
    };

    public static IReadOnlyList<string> Validate(JsonElement body, JsonElement inputSchema)
    {
        var errors = new List<string>();
        var budget = new ValidationBudget();
        ValidateRepresentable(body, errors, budget, depth: 0);
        ValidateNode(body, inputSchema, "$", errors, budget, depth: 0);
        return errors;
    }

    /// <summary>Save-time gate for the author-declared schema; payload validation does not guess unsupported semantics.</summary>
    public static IReadOnlyList<string> ValidateSchema(JsonElement schema)
    {
        var errors = new List<string>();
        var budget = new ValidationBudget();
        ValidateSchemaNode(schema, "$", errors, budget, depth: 0);
        return errors;
    }

    private static void ValidateNode(JsonElement value, JsonElement schema, string path, List<string> errors, ValidationBudget budget, int depth)
    {
        if (errors.Count >= MaxErrors || budget.Exhausted) return;
        if (depth > MaxSchemaDepth)
        {
            errors.Add($"{path}: schema or value is too deep (max {MaxSchemaDepth})");
            return;
        }
        if (++budget.Nodes > MaxNodes)
        {
            errors.Add("$: schema or value contains too many nodes");
            budget.Exhausted = true;
            return;
        }
        if (schema.ValueKind != JsonValueKind.Object) return;

        if (schema.TryGetProperty("type", out var type) && !TypeMatches(value, type))
        {
            errors.Add($"{path}: expected type '{DescribeType(type)}' but got {KindName(value.ValueKind)}");
            return;
        }

        if (schema.TryGetProperty("enum", out var enumValues) && enumValues.ValueKind == JsonValueKind.Array &&
            !enumValues.EnumerateArray().Any(candidate => JsonValueEquals(value, candidate)))
            errors.Add($"{path}: value '{DisplayValue(value)}' is not one of the allowed enum values");

        if (value.ValueKind == JsonValueKind.Object)
        {
            ValidateObject(value, schema, path, errors, budget, depth);
        }
        else if (value.ValueKind == JsonValueKind.Array && schema.TryGetProperty("items", out var items))
        {
            var index = 0;
            foreach (var item in value.EnumerateArray())
            {
                if (errors.Count >= MaxErrors || budget.Exhausted) return;
                ValidateNode(item, items, $"{path}[{index++}]", errors, budget, depth + 1);
            }
        }
        else if (value.ValueKind == JsonValueKind.String && schema.TryGetProperty("maxLength", out var maxLength) &&
                 maxLength.TryGetInt32(out var maximum) && TryGetString(value, out var text) && text.Length > maximum)
        {
            errors.Add($"{path}: string exceeds maximum length {maximum}");
        }
    }

    private static void ValidateObject(JsonElement value, JsonElement schema, string path, List<string> errors, ValidationBudget budget, int depth)
    {
        if (schema.TryGetProperty("required", out var required) && required.ValueKind == JsonValueKind.Array)
        {
            foreach (var property in required.EnumerateArray())
            {
                if (errors.Count >= MaxErrors) return;
                if (property.ValueKind == JsonValueKind.String && !value.TryGetProperty(property.GetString()!, out _))
                    errors.Add($"{path}.{property.GetString()}: required property is missing");
            }
        }

        var hasProperties = schema.TryGetProperty("properties", out var properties) && properties.ValueKind == JsonValueKind.Object;
        var closesObject = schema.TryGetProperty("additionalProperties", out var additionalProperties) &&
                           additionalProperties.ValueKind == JsonValueKind.False;

        foreach (var property in value.EnumerateObject())
        {
            if (errors.Count >= MaxErrors || budget.Exhausted) return;
            if (hasProperties && properties.TryGetProperty(property.Name, out var propertySchema))
                ValidateNode(property.Value, propertySchema, $"{path}.{property.Name}", errors, budget, depth + 1);
            else if (closesObject)
                errors.Add($"{path}.{property.Name}: additional property is not allowed");
        }
    }

    private static void ValidateSchemaNode(JsonElement schema, string path, List<string> errors, ValidationBudget budget, int depth)
    {
        if (errors.Count >= MaxErrors || budget.Exhausted) return;
        if (schema.ValueKind != JsonValueKind.Object) return;
        if (depth > MaxSchemaDepth)
        {
            errors.Add($"{path}: schema is too deep (max {MaxSchemaDepth})");
            return;
        }
        if (++budget.Nodes > MaxNodes)
        {
            errors.Add("$: schema contains too many nodes");
            budget.Exhausted = true;
            return;
        }

        foreach (var keyword in schema.EnumerateObject())
        {
            if (!SupportedSchemaKeywords.Contains(keyword.Name))
                errors.Add($"{path}.{keyword.Name}: schema keyword '{keyword.Name}' is not supported");
        }

        if (schema.TryGetProperty("type", out var type) && !TypeKeywordIsSupported(type))
            errors.Add($"{path}.type: type '{type}' is not supported");

        if (schema.TryGetProperty("additionalProperties", out var additionalProperties) &&
            additionalProperties.ValueKind == JsonValueKind.Object)
            ValidateSchemaNode(additionalProperties, $"{path}.additionalProperties", errors, budget, depth + 1);

        if (schema.TryGetProperty("properties", out var properties) && properties.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in properties.EnumerateObject())
                ValidateSchemaNode(property.Value, $"{path}.properties.{property.Name}", errors, budget, depth + 1);
        }

        if (schema.TryGetProperty("items", out var items))
            ValidateSchemaNode(items, $"{path}.items", errors, budget, depth + 1);
    }

    private static bool TypeKeywordIsSupported(JsonElement type) => type.ValueKind switch
    {
        JsonValueKind.String => IsSupportedType(type.GetString()!),
        JsonValueKind.Array => type.EnumerateArray().All(candidate => candidate.ValueKind == JsonValueKind.String && IsSupportedType(candidate.GetString()!)),
        _ => false,
    };

    private static bool IsSupportedType(string type) => type is "object" or "array" or "string" or "integer" or "number" or "boolean" or "null";

    /// <summary>
    /// jsonb rejects lone surrogates and numeric values it cannot encode. Reject both while the payload is still a
    /// caller-visible 400, instead of letting the write fail as a server error.
    /// </summary>
    private static void ValidateRepresentable(JsonElement value, List<string> errors, ValidationBudget budget, int depth)
    {
        if (errors.Count >= MaxErrors || budget.Exhausted) return;
        if (depth > MaxSchemaDepth)
        {
            errors.Add($"$: payload is too deep (max {MaxSchemaDepth})");
            budget.Exhausted = true;
            return;
        }
        if (++budget.Nodes > MaxNodes)
        {
            errors.Add("$: payload contains too many nodes");
            budget.Exhausted = true;
            return;
        }

        switch (value.ValueKind)
        {
            case JsonValueKind.String:
                if (!TryGetString(value, out var text))
                {
                    errors.Add("$: string is not representable");
                    break;
                }
                if (!IsWellFormedUtf16(text))
                    errors.Add("$: string contains text that cannot be stored");
                break;
            case JsonValueKind.Number:
                var raw = value.GetRawText();
                if (raw.Length > MaxNumberTokenLength)
                {
                    errors.Add("$: number token is too long");
                    break;
                }
                if (!TryGetRepresentableNumber(raw))
                    errors.Add("$: number is not representable");
                break;
            case JsonValueKind.Object:
                foreach (var property in value.EnumerateObject())
                    ValidateRepresentable(property.Value, errors, budget, depth + 1);
                break;
            case JsonValueKind.Array:
                foreach (var item in value.EnumerateArray())
                    ValidateRepresentable(item, errors, budget, depth + 1);
                break;
        }
    }

    private static bool TryGetRepresentableNumber(string raw)
    {
        var exponentIndex = raw.IndexOfAny(['e', 'E']);
        if (exponentIndex >= 0)
        {
            var exponentText = raw[(exponentIndex + 1)..];
            if (exponentText.StartsWith('+') || exponentText.StartsWith('-')) exponentText = exponentText[1..];
            if (exponentText.Length > 20) return false;
        }

        return double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) && double.IsFinite(number);
    }

    private static bool TryGetString(JsonElement value, out string text)
    {
        try { text = value.GetString()!; return true; }
        catch (InvalidOperationException) { text = string.Empty; return false; }
    }

    /// <summary>Postgres jsonb rejects unpaired UTF-16 surrogates; .NET strings can still contain them.</summary>
    private static bool IsWellFormedUtf16(string value)
    {
        for (var i = 0; i < value.Length; i++)
        {
            if (char.IsHighSurrogate(value[i]))
            {
                if (i + 1 >= value.Length || !char.IsLowSurrogate(value[i + 1])) return false;
                i++;
            }
            else if (char.IsLowSurrogate(value[i])) return false;
        }
        return true;
    }

    private static bool TypeMatches(JsonElement value, JsonElement type) => type.ValueKind switch
    {
        JsonValueKind.String => TypeMatches(value, type.GetString()!),
        JsonValueKind.Array => type.EnumerateArray().Any(candidate => candidate.ValueKind == JsonValueKind.String && TypeMatches(value, candidate.GetString()!)),
        _ => true,
    };

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

    private static string DescribeType(JsonElement type)
    {
        var names = type.ValueKind == JsonValueKind.Array
            ? type.EnumerateArray().Where(candidate => candidate.ValueKind == JsonValueKind.String).Select(candidate => candidate.GetString()).ToArray()
            : type.ValueKind == JsonValueKind.String ? [type.GetString()!] : [];

        return names.Length == 0 ? "a valid JSON type (the schema declares none)" : string.Join(" | ", names);
    }

    private static bool IsInteger(string rawNumber)
    {
        var exponentIndex = rawNumber.IndexOfAny(['e', 'E']);
        var mantissa = exponentIndex < 0 ? rawNumber : rawNumber[..exponentIndex];
        var exponent = exponentIndex < 0 ? BigInteger.Zero : BigInteger.Parse(rawNumber[(exponentIndex + 1)..], CultureInfo.InvariantCulture);
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
            JsonValueKind.String => TryGetString(left, out var leftText) && TryGetString(right, out var rightText)
                ? leftText == rightText
                : RawTextEquals(left, right),
            JsonValueKind.Number => left.TryGetDouble(out var leftNumber) && right.TryGetDouble(out var rightNumber) && leftNumber == rightNumber,
            JsonValueKind.True or JsonValueKind.False or JsonValueKind.Null => true,
            _ => RawTextEquals(left, right),
        };
    }

    private static string KindName(JsonValueKind kind) => kind switch
    {
        JsonValueKind.True or JsonValueKind.False => "boolean",
        _ => kind.ToString().ToLowerInvariant(),
    };

    private static string DisplayValue(JsonElement value) => value.ValueKind == JsonValueKind.String && !TryGetString(value, out _)
        ? value.GetRawText()
        : value.ToString();

    /// <summary>Container raw text can still contain an unreadable string escape, so compare tokens without reading strings.</summary>
    private static bool RawTextEquals(JsonElement left, JsonElement right)
    {
        try { return left.GetRawText() == right.GetRawText(); }
        catch (InvalidOperationException) { return false; }
    }

    private sealed class ValidationBudget
    {
        public int Nodes { get; set; }
        public bool Exhausted { get; set; }
    }
}
