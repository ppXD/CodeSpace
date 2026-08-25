using System.Text.Json;

namespace CodeSpace.Core.Services.Workflows.Runtime;

/// <summary>
/// Masks exact secret values only at persistence boundaries. Runtime values are never mutated, so downstream nodes
/// and durable recovery can continue with the original values through the encrypted sensitive-payload sidecar.
/// </summary>
public sealed class PersistenceSecretRedactor
{
    public const string Marker = "[REDACTED]";

    private readonly IReadOnlyList<string> _secrets;

    public PersistenceSecretRedactor(IEnumerable<string> secrets) => _secrets = secrets
        .Where(value => !string.IsNullOrEmpty(value))
        .Distinct(StringComparer.Ordinal)
        .OrderByDescending(value => value.Length)
        .ToArray();

    public static PersistenceSecretRedactor FromScope(NodeRunScope scope)
    {
        var secrets = new List<string>();
        foreach (var path in scope.SecretPaths)
        {
            if (TryResolve(scope, path, out var value)) CollectScalarValues(value, secrets);
        }
        return new PersistenceSecretRedactor(secrets);
    }

    public PersistenceRedaction<string?> Redact(string? value)
    {
        if (string.IsNullOrEmpty(value)) return new PersistenceRedaction<string?>(value, false);

        var redacted = value;
        foreach (var secret in _secrets) redacted = redacted.Replace(secret, Marker, StringComparison.Ordinal);
        return new PersistenceRedaction<string?>(redacted, !string.Equals(value, redacted, StringComparison.Ordinal));
    }

    public PersistenceRedaction<JsonElement> Redact(JsonElement value)
    {
        var changed = false;
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream)) WriteRedacted(writer, value, ref changed);
        return new PersistenceRedaction<JsonElement>(JsonDocument.Parse(stream.ToArray()).RootElement.Clone(), changed);
    }

    public PersistenceRedaction<IReadOnlyDictionary<string, JsonElement>> Redact(IReadOnlyDictionary<string, JsonElement> values)
    {
        var changed = false;
        var redacted = new Dictionary<string, JsonElement>(values.Count, StringComparer.Ordinal);
        foreach (var (key, value) in values)
        {
            var keyResult = Redact(key);
            var valueResult = Redact(value);
            redacted[keyResult.Value ?? key] = valueResult.Value;
            changed |= keyResult.Changed || valueResult.Changed;
        }
        return new PersistenceRedaction<IReadOnlyDictionary<string, JsonElement>>(redacted, changed);
    }

    private void WriteRedacted(Utf8JsonWriter writer, JsonElement value, ref bool changed)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in value.EnumerateObject())
                {
                    var propertyName = Redact(property.Name);
                    writer.WritePropertyName(propertyName.Value ?? property.Name);
                    changed |= propertyName.Changed;
                    WriteRedacted(writer, property.Value, ref changed);
                }
                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in value.EnumerateArray()) WriteRedacted(writer, item, ref changed);
                writer.WriteEndArray();
                break;
            case JsonValueKind.String:
                var stringResult = Redact(value.GetString());
                writer.WriteStringValue(stringResult.Value);
                changed |= stringResult.Changed;
                break;
            default:
                if (_secrets.Contains(value.GetRawText(), StringComparer.Ordinal))
                {
                    writer.WriteStringValue(Marker);
                    changed = true;
                }
                else
                {
                    value.WriteTo(writer);
                }
                break;
        }
    }

    private static bool TryResolve(NodeRunScope scope, string path, out JsonElement value)
    {
        value = default;
        var segments = path.Split('.');

        if (segments.Length == 2 && segments[0] == "team") return scope.Team.TryGetValue(segments[1], out value);
        if (segments.Length == 2 && segments[0] == "wf") return scope.Wf.TryGetValue(segments[1], out value);
        return segments.Length == 3 && segments[0] == "project" && scope.Projects.TryGetValue(segments[1], out var project) && project.TryGetValue(segments[2], out value);
    }

    private static void CollectScalarValues(JsonElement value, ICollection<string> destination)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in value.EnumerateObject()) CollectScalarValues(property.Value, destination);
                break;
            case JsonValueKind.Array:
                foreach (var item in value.EnumerateArray()) CollectScalarValues(item, destination);
                break;
            case JsonValueKind.String:
                if (value.GetString() is { Length: > 0 } text) destination.Add(text);
                break;
            case JsonValueKind.Number:
            case JsonValueKind.True:
            case JsonValueKind.False:
                destination.Add(value.GetRawText());
                break;
        }
    }
}

public readonly record struct PersistenceRedaction<T>(T Value, bool Changed);
