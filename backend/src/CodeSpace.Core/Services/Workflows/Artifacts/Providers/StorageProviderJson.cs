using System.Buffers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace CodeSpace.Core.Services.Workflows.Artifacts.Providers;

/// <summary>
/// Strict JSON-Schema admission and deterministic JSON serialization for provider-owned configuration boundaries.
/// Unsupported vocabulary is an error: a provider assertion this build cannot enforce must never become success.
/// </summary>
internal static class StorageProviderJson
{
    internal const int MaxValueBytes = 64 * 1024;
    internal const int MaxValueDepth = 16;
    internal const int MaxValueNodes = 4096;

    private static readonly HashSet<string> SupportedSchemaKeywords = new(StringComparer.Ordinal)
    {
        "$schema", "$id", "$comment", "title", "description", "default", "examples", "deprecated", "readOnly", "writeOnly",
        "type", "properties", "required", "additionalProperties", "items", "enum", "const", "pattern",
        "minLength", "maxLength", "minimum", "maximum", "minItems", "maxItems",
    };

    public static void Validate(JsonElement value, JsonElement schema, string valueName, string schemaName = "SecretSchema")
    {
        EnsureWithinLimits(value, valueName);
        ValidateSchema(schema, schemaName);
        ValidateNode(value, schema, "$", valueName);
    }

    public static void ValidateSchema(JsonElement schema, string path)
    {
        EnsureWithinLimits(schema, path);
        ValidateSchemaNode(schema, path);
    }

    private static void ValidateSchemaNode(JsonElement schema, string path)
    {
        if (schema.ValueKind is JsonValueKind.True or JsonValueKind.False) return;
        if (schema.ValueKind != JsonValueKind.Object) throw new ArgumentException($"Storage provider schema at {path} must be an object or boolean.");
        EnsureUniqueProperties(schema, path, "Storage provider schema");

        foreach (var keyword in schema.EnumerateObject())
        {
            if (!SupportedSchemaKeywords.Contains(keyword.Name))
                throw new ArgumentException($"Storage provider schema keyword '{keyword.Name}' at {path} is unsupported and cannot be treated as validation success.");
        }

        ValidateTypeSchema(schema, path);
        ValidateRequiredSchema(schema, path);
        ValidatePropertiesSchema(schema, path);
        ValidateItemsSchema(schema, path);
        ValidateAssertionSchema(schema, path);
    }

    public static string Canonicalize(JsonElement value, string valueName)
    {
        EnsureWithinLimits(value, valueName);
        var output = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(output, new JsonWriterOptions { Indented = false, SkipValidation = false }))
            WriteCanonical(writer, value, valueName);
        return Encoding.UTF8.GetString(output.WrittenSpan);
    }

    private static void ValidateNode(JsonElement value, JsonElement schema, string path, string valueName)
    {
        if (schema.ValueKind == JsonValueKind.True) return;
        if (schema.ValueKind == JsonValueKind.False) throw new ArgumentException($"{valueName} property '{path}' is forbidden by the provider schema.");
        if (schema.ValueKind != JsonValueKind.Object) throw new ArgumentException($"Storage provider schema at {path} must be an object or boolean.");
        EnsureType(value, schema, path, valueName);
        EnsureValueAssertions(value, schema, path, valueName);

        if (value.ValueKind == JsonValueKind.Object)
        {
            EnsureUniqueProperties(value, path, valueName);
            var properties = schema.TryGetProperty("properties", out var declared) && declared.ValueKind == JsonValueKind.Object ? declared : default;
            if (schema.TryGetProperty("required", out var required))
            {
                foreach (var item in required.EnumerateArray())
                {
                    var name = item.GetString()!;
                    if (!value.TryGetProperty(name, out _)) throw new ArgumentException($"{valueName} is missing required property '{Path(path, name)}'.");
                }
            }

            var additional = schema.TryGetProperty("additionalProperties", out var additionalProperties) ? additionalProperties : default;
            foreach (var property in value.EnumerateObject())
            {
                if (properties.ValueKind == JsonValueKind.Object && properties.TryGetProperty(property.Name, out var propertySchema))
                {
                    ValidateNode(property.Value, propertySchema, Path(path, property.Name), valueName);
                    continue;
                }

                if (additional.ValueKind == JsonValueKind.False)
                    throw new ArgumentException($"{valueName} property '{Path(path, property.Name)}' is not allowed by the provider schema.");
                if (additional.ValueKind is JsonValueKind.Object or JsonValueKind.True)
                    ValidateNode(property.Value, additional, Path(path, property.Name), valueName);
            }
        }

        if (value.ValueKind == JsonValueKind.Array && schema.TryGetProperty("items", out var items))
        {
            var index = 0;
            foreach (var item in value.EnumerateArray()) ValidateNode(item, items, $"{path}[{index++}]", valueName);
        }
    }

    private static void EnsureType(JsonElement value, JsonElement schema, string path, string valueName)
    {
        if (!schema.TryGetProperty("type", out var type)) return;
        var accepted = TypeNames(type, path);
        if (!accepted.Any(candidate => MatchesType(value, candidate)))
            throw new ArgumentException($"{valueName} property '{path}' must be of type {string.Join(" or ", accepted)}.");
    }

    private static string[] TypeNames(JsonElement type, string path) => type.ValueKind switch
    {
        JsonValueKind.String => [type.GetString()!],
        JsonValueKind.Array => type.EnumerateArray().Select(value => value.ValueKind == JsonValueKind.String ? value.GetString()! : throw new ArgumentException($"Storage provider schema type at {path} must contain strings.")).ToArray(),
        _ => throw new ArgumentException($"Storage provider schema type at {path} must be a string or array."),
    };

    private static bool MatchesType(JsonElement value, string type) => type switch
    {
        "object" => value.ValueKind == JsonValueKind.Object,
        "array" => value.ValueKind == JsonValueKind.Array,
        "string" => value.ValueKind == JsonValueKind.String,
        "number" => value.ValueKind == JsonValueKind.Number,
        "integer" => value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out var number) && decimal.Truncate(number) == number,
        "boolean" => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
        "null" => value.ValueKind == JsonValueKind.Null,
        _ => throw new ArgumentException($"Storage provider schema declares unsupported type '{type}'."),
    };

    private static void ValidateTypeSchema(JsonElement schema, string path)
    {
        if (!schema.TryGetProperty("type", out var type)) return;
        var values = TypeNames(type, path);
        if (values.Length == 0 || values.Distinct(StringComparer.Ordinal).Count() != values.Length)
            throw new ArgumentException($"Storage provider schema type at {path} must contain distinct supported values.");
        foreach (var value in values) _ = MatchesType(default, value);
    }

    private static void ValidateRequiredSchema(JsonElement schema, string path)
    {
        if (!schema.TryGetProperty("required", out var required)) return;
        if (required.ValueKind != JsonValueKind.Array) throw new ArgumentException($"Storage provider schema required at {path} must be an array.");
        var names = required.EnumerateArray().Select(value => value.ValueKind == JsonValueKind.String ? value.GetString()! : throw new ArgumentException($"Storage provider schema required at {path} must contain strings.")).ToList();
        if (names.Distinct(StringComparer.Ordinal).Count() != names.Count)
            throw new ArgumentException($"Storage provider schema required at {path} must not contain duplicates.");
    }

    private static void ValidatePropertiesSchema(JsonElement schema, string path)
    {
        if (schema.TryGetProperty("properties", out var properties))
        {
            if (properties.ValueKind != JsonValueKind.Object) throw new ArgumentException($"Storage provider schema properties at {path} must be an object.");
            EnsureUniqueProperties(properties, $"{path}.properties", "Storage provider schema");
            foreach (var property in properties.EnumerateObject()) ValidateSchemaNode(property.Value, Path(path, property.Name));
        }

        if (!schema.TryGetProperty("additionalProperties", out var additional)) return;
        if (additional.ValueKind is not (JsonValueKind.Object or JsonValueKind.True or JsonValueKind.False))
            throw new ArgumentException($"Storage provider schema additionalProperties at {path} must be an object or boolean.");
        if (additional.ValueKind == JsonValueKind.Object) ValidateSchemaNode(additional, $"{path}.additionalProperties");
    }

    private static void ValidateItemsSchema(JsonElement schema, string path)
    {
        if (!schema.TryGetProperty("items", out var items)) return;
        if (items.ValueKind is not (JsonValueKind.Object or JsonValueKind.True or JsonValueKind.False))
            throw new ArgumentException($"Storage provider schema items at {path} must be an object or boolean.");
        if (items.ValueKind == JsonValueKind.Object) ValidateSchemaNode(items, $"{path}.items");
    }

    private static void ValidateAssertionSchema(JsonElement schema, string path)
    {
        if (schema.TryGetProperty("enum", out var enumeration))
        {
            if (enumeration.ValueKind != JsonValueKind.Array || enumeration.GetArrayLength() == 0)
                throw new ArgumentException($"Storage provider schema enum at {path} must be a non-empty array.");
            var values = enumeration.EnumerateArray().ToList();
            if (values.Where((candidate, index) => values.Take(index).Any(prior => JsonElement.DeepEquals(prior, candidate))).Any())
                throw new ArgumentException($"Storage provider schema enum at {path} must not contain duplicates.");
        }

        if (schema.TryGetProperty("pattern", out var pattern))
        {
            if (pattern.ValueKind != JsonValueKind.String) throw new ArgumentException($"Storage provider schema pattern at {path} must be a string.");
            try { _ = new Regex(pattern.GetString()!, RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100)); }
            catch (ArgumentException exception) { throw new ArgumentException($"Storage provider schema pattern at {path} is invalid.", exception); }
        }

        ValidateNonNegativeInteger(schema, "minLength", path);
        ValidateNonNegativeInteger(schema, "maxLength", path);
        ValidateNonNegativeInteger(schema, "minItems", path);
        ValidateNonNegativeInteger(schema, "maxItems", path);
        ValidateNumber(schema, "minimum", path);
        ValidateNumber(schema, "maximum", path);
    }

    private static void EnsureValueAssertions(JsonElement value, JsonElement schema, string path, string valueName)
    {
        if (schema.TryGetProperty("enum", out var enumeration) && !enumeration.EnumerateArray().Any(candidate => JsonElement.DeepEquals(candidate, value)))
            throw new ArgumentException($"{valueName} property '{path}' is not one of the provider's allowed values.");
        if (schema.TryGetProperty("const", out var constant) && !JsonElement.DeepEquals(constant, value))
            throw new ArgumentException($"{valueName} property '{path}' must equal the provider's fixed value.");

        if (value.ValueKind == JsonValueKind.String) EnsureStringAssertions(value.GetString()!, schema, path, valueName);
        if (value.ValueKind == JsonValueKind.Number) EnsureNumberAssertions(value, schema, path, valueName);
        if (value.ValueKind == JsonValueKind.Array) EnsureArrayAssertions(value.GetArrayLength(), schema, path, valueName);
    }

    private static void EnsureStringAssertions(string value, JsonElement schema, string path, string valueName)
    {
        var length = value.EnumerateRunes().Count();
        if (schema.TryGetProperty("minLength", out var minimum) && length < minimum.GetInt32())
            throw new ArgumentException($"{valueName} property '{path}' must contain at least {minimum.GetInt32()} characters.");
        if (schema.TryGetProperty("maxLength", out var maximum) && length > maximum.GetInt32())
            throw new ArgumentException($"{valueName} property '{path}' must contain at most {maximum.GetInt32()} characters.");
        if (!schema.TryGetProperty("pattern", out var pattern)) return;
        try
        {
            if (!Regex.IsMatch(value, pattern.GetString()!, RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100)))
                throw new ArgumentException($"{valueName} property '{path}' does not match the provider pattern.");
        }
        catch (RegexMatchTimeoutException exception)
        {
            throw new ArgumentException($"Storage provider pattern at {path} could not be evaluated safely.", exception);
        }
    }

    private static void EnsureNumberAssertions(JsonElement value, JsonElement schema, string path, string valueName)
    {
        if (!value.TryGetDecimal(out var number)) throw new ArgumentException($"{valueName} property '{path}' is outside the supported numeric range.");
        if (schema.TryGetProperty("minimum", out var minimum) && number < minimum.GetDecimal())
            throw new ArgumentException($"{valueName} property '{path}' must be at least {minimum.GetRawText()}.");
        if (schema.TryGetProperty("maximum", out var maximum) && number > maximum.GetDecimal())
            throw new ArgumentException($"{valueName} property '{path}' must be at most {maximum.GetRawText()}.");
    }

    private static void EnsureArrayAssertions(int count, JsonElement schema, string path, string valueName)
    {
        if (schema.TryGetProperty("minItems", out var minimum) && count < minimum.GetInt32())
            throw new ArgumentException($"{valueName} property '{path}' must contain at least {minimum.GetInt32()} items.");
        if (schema.TryGetProperty("maxItems", out var maximum) && count > maximum.GetInt32())
            throw new ArgumentException($"{valueName} property '{path}' must contain at most {maximum.GetInt32()} items.");
    }

    private static void ValidateNonNegativeInteger(JsonElement schema, string keyword, string path)
    {
        if (!schema.TryGetProperty(keyword, out var value)) return;
        if (!value.TryGetInt32(out var number) || number < 0)
            throw new ArgumentException($"Storage provider schema {keyword} at {path} must be a non-negative integer.");
    }

    private static void ValidateNumber(JsonElement schema, string keyword, string path)
    {
        if (!schema.TryGetProperty(keyword, out var value)) return;
        if (!value.TryGetDecimal(out _)) throw new ArgumentException($"Storage provider schema {keyword} at {path} must be a supported number.");
    }

    private static void EnsureUniqueProperties(JsonElement value, string path, string valueName)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject())
        {
            if (!names.Add(property.Name)) throw new ArgumentException($"{valueName} contains duplicate property '{Path(path, property.Name)}'.");
        }
    }

    private static void EnsureWithinLimits(JsonElement value, string valueName)
    {
        var raw = value.GetRawText();
        if (raw.Length > MaxValueBytes || Encoding.UTF8.GetByteCount(raw) > MaxValueBytes)
            throw new ArgumentException($"{valueName} must not exceed {MaxValueBytes} UTF-8 bytes.");

        var nodes = 0;
        var pending = new Stack<(JsonElement Value, int Depth)>();
        pending.Push((value, 1));
        while (pending.TryPop(out var current))
        {
            if (current.Depth > MaxValueDepth) throw new ArgumentException($"{valueName} must not exceed a JSON depth of {MaxValueDepth}.");
            if (++nodes > MaxValueNodes) throw new ArgumentException($"{valueName} must not exceed {MaxValueNodes} JSON nodes.");

            if (current.Value.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in current.Value.EnumerateObject()) pending.Push((property.Value, current.Depth + 1));
            }
            else if (current.Value.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in current.Value.EnumerateArray()) pending.Push((item, current.Depth + 1));
            }
        }
    }

    private static void WriteCanonical(Utf8JsonWriter writer, JsonElement value, string valueName)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                EnsureUniqueProperties(value, "$canonical", valueName);
                writer.WriteStartObject();
                foreach (var property in value.EnumerateObject().OrderBy(property => property.Name, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Name);
                    WriteCanonical(writer, property.Value, valueName);
                }
                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in value.EnumerateArray()) WriteCanonical(writer, item, valueName);
                writer.WriteEndArray();
                break;
            default:
                value.WriteTo(writer);
                break;
        }
    }

    private static string Path(string parent, string property) => parent == "$" ? property : $"{parent}.{property}";
}
