using System.Buffers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using CodeSpace.Core.Persistence.Entities;

namespace CodeSpace.Core.Services.Workflows.Artifacts.Profiles;

/// <summary>Pure, deterministic admission rules shared by the storage-profile control plane and its tests.</summary>
internal static class StorageProfileRules
{
    private static readonly Regex StableNamePattern = new("^[a-z0-9][a-z0-9-]{0,127}$", RegexOptions.CultureInvariant);
    private static readonly HashSet<string> SupportedSchemaKeywords = new(StringComparer.Ordinal)
    {
        "$schema", "$id", "$comment", "title", "description", "default", "examples", "deprecated", "readOnly", "writeOnly",
        "type", "properties", "required", "additionalProperties", "items", "enum", "const", "pattern",
        "minLength", "maxLength", "minimum", "maximum", "minItems", "maxItems",
    };

    public static string NormalizeStableName(string? value)
    {
        var normalized = value?.Trim().ToLowerInvariant() ?? string.Empty;
        if (!StableNamePattern.IsMatch(normalized)) throw new ArgumentException("StableName must be 1-128 lowercase letters, digits, or hyphens and must start with a letter or digit.");
        return normalized;
    }

    public static void ValidateConfig(JsonElement config, JsonElement configSchema, JsonElement secretSchema)
    {
        if (config.ValueKind != JsonValueKind.Object) throw new ArgumentException("NonSecretConfig must be a JSON object.");
        ValidateSchema(configSchema, "ConfigSchema");
        ValidateSchema(secretSchema, "SecretSchema");
        RejectSecretProperties(config, secretSchema, "$");
        ValidateNode(config, configSchema, "$");
    }

    public static string CanonicalJson(JsonElement value)
    {
        var output = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(output, new JsonWriterOptions { Indented = false, SkipValidation = false }))
            WriteCanonical(writer, value);
        return Encoding.UTF8.GetString(output.WrittenSpan);
    }

    public static string NamespaceFingerprint(string providerTypeKey, JsonElement namespaceConfig)
    {
        var canonical = CanonicalJson(namespaceConfig);
        var input = Encoding.UTF8.GetBytes($"storage-namespace/v1\n{providerTypeKey}\n{canonical}");
        return "sha256:" + Convert.ToHexString(SHA256.HashData(input)).ToLowerInvariant();
    }

    public static bool TryParseCredentialRef(string? value, out StorageProfileCredentialReference reference)
    {
        reference = default;
        if (string.IsNullOrWhiteSpace(value)) return false;

        var parts = value.Split(':', StringSplitOptions.None);
        if (parts.Length != 3 || parts[0] != "db" || !Guid.TryParseExact(parts[1], "D", out var id) || id == Guid.Empty
            || !int.TryParse(parts[2], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var revision) || revision <= 0)
            return false;

        reference = new StorageProfileCredentialReference(id, revision);
        return true;
    }

    public static void EnsureRevisionAllowed(StorageProfileState state)
    {
        if (state == StorageProfileState.Retired) throw new ArgumentException("A retired storage profile is terminal and cannot receive a new revision.");
    }

    public static void EnsureTransition(StorageProfileState current, StorageProfileState requested)
    {
        if (current == requested) return;
        if (current == StorageProfileState.Retired) throw new ArgumentException("A retired storage profile is terminal and cannot change state.");
        if (requested == StorageProfileState.Draft) throw new ArgumentException("A storage profile cannot transition back to Draft.");
        if (!Enum.IsDefined(requested)) throw new ArgumentException($"Storage profile state '{requested}' is not supported.");
    }

    private static void ValidateNode(JsonElement value, JsonElement schema, string path)
    {
        if (schema.ValueKind == JsonValueKind.True) return;
        if (schema.ValueKind == JsonValueKind.False) throw new ArgumentException($"NonSecretConfig property '{path}' is forbidden by the provider schema.");
        if (schema.ValueKind != JsonValueKind.Object) throw new ArgumentException($"Storage provider schema at {path} must be an object or boolean.");
        EnsureType(value, schema, path);
        EnsureValueAssertions(value, schema, path);

        if (value.ValueKind == JsonValueKind.Object)
        {
            EnsureUniqueProperties(value, path);
            var properties = schema.TryGetProperty("properties", out var declared) && declared.ValueKind == JsonValueKind.Object ? declared : default;
            if (schema.TryGetProperty("required", out var required))
            {
                foreach (var item in required.EnumerateArray())
                {
                    var name = item.GetString()!;
                    if (!value.TryGetProperty(name, out _)) throw new ArgumentException($"NonSecretConfig is missing required property '{Path(path, name)}'.");
                }
            }

            var additional = schema.TryGetProperty("additionalProperties", out var additionalProperties) ? additionalProperties : default;
            foreach (var property in value.EnumerateObject())
            {
                if (properties.ValueKind == JsonValueKind.Object && properties.TryGetProperty(property.Name, out var propertySchema))
                {
                    ValidateNode(property.Value, propertySchema, Path(path, property.Name));
                    continue;
                }

                if (additional.ValueKind is JsonValueKind.False)
                    throw new ArgumentException($"NonSecretConfig property '{Path(path, property.Name)}' is not allowed by the provider schema.");
                if (additional.ValueKind is JsonValueKind.Object or JsonValueKind.True)
                    ValidateNode(property.Value, additional, Path(path, property.Name));
            }
        }

        if (value.ValueKind == JsonValueKind.Array && schema.TryGetProperty("items", out var items))
        {
            var index = 0;
            foreach (var item in value.EnumerateArray()) ValidateNode(item, items, $"{path}[{index++}]");
        }
    }

    private static void EnsureType(JsonElement value, JsonElement schema, string path)
    {
        if (!schema.TryGetProperty("type", out var type)) return;
        var accepted = type.ValueKind switch
        {
            JsonValueKind.String => new[] { type.GetString()! },
            JsonValueKind.Array => type.EnumerateArray().Select(item => item.GetString()!).ToArray(),
            _ => throw new ArgumentException($"Storage provider schema type at {path} must be a string or array."),
        };

        if (!accepted.Any(candidate => MatchesType(value, candidate)))
            throw new ArgumentException($"NonSecretConfig property '{path}' must be of type {string.Join(" or ", accepted)}.");
    }

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

    private static void RejectSecretProperties(JsonElement config, JsonElement secretSchema, string path)
    {
        if (config.ValueKind != JsonValueKind.Object || secretSchema.ValueKind != JsonValueKind.Object) return;
        if (!secretSchema.TryGetProperty("properties", out var properties) || properties.ValueKind != JsonValueKind.Object) return;

        foreach (var secret in properties.EnumerateObject())
        {
            if (!config.TryGetProperty(secret.Name, out var candidate)) continue;
            RejectSecretValue(candidate, secret.Value, Path(path, secret.Name));
        }
    }

    private static void RejectSecretValue(JsonElement candidate, JsonElement secretSchema, string path)
    {
        if (candidate.ValueKind == JsonValueKind.Object && secretSchema.ValueKind == JsonValueKind.Object
            && secretSchema.TryGetProperty("properties", out var nested) && nested.ValueKind == JsonValueKind.Object && nested.EnumerateObject().Any())
        {
            RejectSecretProperties(candidate, secretSchema, path);
            return;
        }

        if (candidate.ValueKind == JsonValueKind.Array && secretSchema.ValueKind == JsonValueKind.Object
            && secretSchema.TryGetProperty("items", out var items))
        {
            var index = 0;
            foreach (var item in candidate.EnumerateArray()) RejectSecretValue(item, items, $"{path}[{index++}]");
            return;
        }

        throw new ArgumentException($"NonSecretConfig property '{path}' is a secret input and must be stored in a StorageCredential, never a profile revision.");
    }

    private static void ValidateSchema(JsonElement schema, string path)
    {
        if (schema.ValueKind is JsonValueKind.True or JsonValueKind.False) return;
        if (schema.ValueKind != JsonValueKind.Object) throw new ArgumentException($"Storage provider schema at {path} must be an object or boolean.");
        EnsureUniqueProperties(schema, path);

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

    private static void ValidateTypeSchema(JsonElement schema, string path)
    {
        if (!schema.TryGetProperty("type", out var type)) return;
        var values = type.ValueKind switch
        {
            JsonValueKind.String => new[] { type.GetString()! },
            JsonValueKind.Array => type.EnumerateArray().Select(value => value.ValueKind == JsonValueKind.String ? value.GetString()! : throw new ArgumentException($"Storage provider schema type at {path} must contain strings.")).ToArray(),
            _ => throw new ArgumentException($"Storage provider schema type at {path} must be a string or array."),
        };
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
            EnsureUniqueProperties(properties, $"{path}.properties");
            foreach (var property in properties.EnumerateObject()) ValidateSchema(property.Value, Path(path, property.Name));
        }

        if (!schema.TryGetProperty("additionalProperties", out var additional)) return;
        if (additional.ValueKind is not (JsonValueKind.Object or JsonValueKind.True or JsonValueKind.False))
            throw new ArgumentException($"Storage provider schema additionalProperties at {path} must be an object or boolean.");
        if (additional.ValueKind == JsonValueKind.Object) ValidateSchema(additional, $"{path}.additionalProperties");
    }

    private static void ValidateItemsSchema(JsonElement schema, string path)
    {
        if (!schema.TryGetProperty("items", out var items)) return;
        if (items.ValueKind is not (JsonValueKind.Object or JsonValueKind.True or JsonValueKind.False))
            throw new ArgumentException($"Storage provider schema items at {path} must be an object or boolean.");
        if (items.ValueKind == JsonValueKind.Object) ValidateSchema(items, $"{path}.items");
    }

    private static void ValidateAssertionSchema(JsonElement schema, string path)
    {
        if (schema.TryGetProperty("enum", out var enumeration) && (enumeration.ValueKind != JsonValueKind.Array || enumeration.GetArrayLength() == 0))
            throw new ArgumentException($"Storage provider schema enum at {path} must be a non-empty array.");
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

    private static void EnsureValueAssertions(JsonElement value, JsonElement schema, string path)
    {
        if (schema.TryGetProperty("enum", out var enumeration) && !enumeration.EnumerateArray().Any(candidate => JsonElement.DeepEquals(candidate, value)))
            throw new ArgumentException($"NonSecretConfig property '{path}' is not one of the provider's allowed values.");
        if (schema.TryGetProperty("const", out var constant) && !JsonElement.DeepEquals(constant, value))
            throw new ArgumentException($"NonSecretConfig property '{path}' must equal the provider's fixed value.");

        if (value.ValueKind == JsonValueKind.String) EnsureStringAssertions(value.GetString()!, schema, path);
        if (value.ValueKind == JsonValueKind.Number) EnsureNumberAssertions(value, schema, path);
        if (value.ValueKind == JsonValueKind.Array) EnsureArrayAssertions(value.GetArrayLength(), schema, path);
    }

    private static void EnsureStringAssertions(string value, JsonElement schema, string path)
    {
        var length = value.EnumerateRunes().Count();
        if (schema.TryGetProperty("minLength", out var minimum) && length < minimum.GetInt32())
            throw new ArgumentException($"NonSecretConfig property '{path}' must contain at least {minimum.GetInt32()} characters.");
        if (schema.TryGetProperty("maxLength", out var maximum) && length > maximum.GetInt32())
            throw new ArgumentException($"NonSecretConfig property '{path}' must contain at most {maximum.GetInt32()} characters.");
        if (schema.TryGetProperty("pattern", out var pattern) && !Regex.IsMatch(value, pattern.GetString()!, RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100)))
            throw new ArgumentException($"NonSecretConfig property '{path}' does not match the provider pattern.");
    }

    private static void EnsureNumberAssertions(JsonElement value, JsonElement schema, string path)
    {
        if (!value.TryGetDecimal(out var number)) throw new ArgumentException($"NonSecretConfig property '{path}' is outside the supported numeric range.");
        if (schema.TryGetProperty("minimum", out var minimum) && number < minimum.GetDecimal())
            throw new ArgumentException($"NonSecretConfig property '{path}' must be at least {minimum.GetRawText()}.");
        if (schema.TryGetProperty("maximum", out var maximum) && number > maximum.GetDecimal())
            throw new ArgumentException($"NonSecretConfig property '{path}' must be at most {maximum.GetRawText()}.");
    }

    private static void EnsureArrayAssertions(int count, JsonElement schema, string path)
    {
        if (schema.TryGetProperty("minItems", out var minimum) && count < minimum.GetInt32())
            throw new ArgumentException($"NonSecretConfig property '{path}' must contain at least {minimum.GetInt32()} items.");
        if (schema.TryGetProperty("maxItems", out var maximum) && count > maximum.GetInt32())
            throw new ArgumentException($"NonSecretConfig property '{path}' must contain at most {maximum.GetInt32()} items.");
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

    private static void EnsureUniqueProperties(JsonElement value, string path)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject())
        {
            if (!names.Add(property.Name)) throw new ArgumentException($"NonSecretConfig contains duplicate property '{Path(path, property.Name)}'.");
        }
    }

    private static void WriteCanonical(Utf8JsonWriter writer, JsonElement value)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                EnsureUniqueProperties(value, "$canonical");
                writer.WriteStartObject();
                foreach (var property in value.EnumerateObject().OrderBy(property => property.Name, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Name);
                    WriteCanonical(writer, property.Value);
                }
                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in value.EnumerateArray()) WriteCanonical(writer, item);
                writer.WriteEndArray();
                break;
            default:
                value.WriteTo(writer);
                break;
        }
    }

    private static string Path(string parent, string property) => parent == "$" ? property : $"{parent}.{property}";
}

internal readonly record struct StorageProfileCredentialReference(Guid Id, int Revision)
{
    public string Canonical => $"db:{Id:D}:{Revision}";
}
