using System.Text.Json;
using CodeSpace.Core.Services.Redaction;

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

    /// <summary>A redactor with no secrets: every value passes through unchanged.</summary>
    public static PersistenceSecretRedactor None { get; } = new([]);

    /// <summary>One redactor masking every secret of each given one; an absent redactor contributes nothing.</summary>
    public static PersistenceSecretRedactor Union(params PersistenceSecretRedactor?[] redactors) => new(redactors.OfType<PersistenceSecretRedactor>().SelectMany(redactor => redactor._secrets));

    public static PersistenceSecretRedactor FromScope(NodeRunScope scope)
    {
        var secrets = new List<string>(scope.InheritedSecretValues);
        foreach (var path in scope.SecretPaths)
        {
            if (TryResolve(scope, path, out var value)) CollectScalarValues(value, secrets);
        }
        return new PersistenceSecretRedactor(secrets);
    }

    /// <summary>
    /// The secret values that occur anywhere in <paramref name="value"/> — inside a string or a property name, or as an
    /// exact non-string scalar: precisely the ones <see cref="Redact(JsonElement)"/> would mask. A payload that crosses
    /// into another run (an agent's task, a child's inputs) carries exactly these, so the receiving run treats as
    /// secret what it was handed and nothing more of this scope.
    /// </summary>
    public IReadOnlyList<string> FoundIn(JsonElement value)
    {
        var texts = new List<string>();
        var scalars = new HashSet<string>(StringComparer.Ordinal);
        CollectTexts(value, texts, scalars);
        return _secrets.Where(secret => scalars.Contains(secret) || texts.Any(text => text.Contains(secret, StringComparison.Ordinal))).ToArray();
    }

    public PersistenceRedaction<string?> Redact(string? value)
    {
        if (string.IsNullOrEmpty(value)) return new PersistenceRedaction<string?>(value, false);

        var redacted = value;
        foreach (var secret in _secrets) redacted = redacted.Replace(secret, Marker, StringComparison.Ordinal);
        return new PersistenceRedaction<string?>(redacted, !string.Equals(value, redacted, StringComparison.Ordinal));
    }

    /// <summary>
    /// How much of <paramref name="text"/>, a text that may still continue, can be redacted on its own: the longest prefix
    /// that no secret runs across and after which nothing could yet become one. A writer persisting a growing text in
    /// pieces releases this much and carries the rest into the next piece, because exact matching cannot recognise a
    /// secret split between two of them.
    /// </summary>
    public int SettledLength(string text) => SecretBoundary.SettledLength(text, _secrets);

    /// <summary>
    /// Redacts a text whose END was cut: a capture that stopped at its budget while the source went on. Besides every
    /// whole secret, a trailing fragment that could be the start of one is masked, because the rest of it was cut away
    /// and exact matching will never see it whole.
    /// </summary>
    public PersistenceRedaction<string?> RedactCut(string? value)
    {
        if (string.IsNullOrEmpty(value)) return new PersistenceRedaction<string?>(value, false);

        var settled = SettledLength(value);

        if (settled == value.Length) return Redact(value);

        return new PersistenceRedaction<string?>(Redact(value[..settled]).Value + Marker, true);
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

    private static void CollectTexts(JsonElement value, ICollection<string> texts, ISet<string> scalars)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in value.EnumerateObject())
                {
                    texts.Add(property.Name);
                    CollectTexts(property.Value, texts, scalars);
                }
                break;
            case JsonValueKind.Array:
                foreach (var item in value.EnumerateArray()) CollectTexts(item, texts, scalars);
                break;
            case JsonValueKind.String:
                texts.Add(value.GetString() ?? "");
                break;
            case JsonValueKind.Number:
            case JsonValueKind.True:
            case JsonValueKind.False:
                scalars.Add(value.GetRawText());
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
