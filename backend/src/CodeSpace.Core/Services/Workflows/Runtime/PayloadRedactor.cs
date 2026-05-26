using System.Text.Json;
using CodeSpace.Core.DependencyInjection;
using CodeSpace.Messages.Dtos.Workflows;

namespace CodeSpace.Core.Services.Workflows.Runtime;

/// <summary>
/// Default <see cref="IPayloadRedactor"/> — template-driven, smallest-containing-key
/// granularity via recursive descent. See <see cref="IPayloadRedactor"/> docs for the
/// policy specification and <see cref="PayloadRedactorTests"/> for pinned behaviour.
///
/// <para>The algorithm walks the template + resolved value in parallel:</para>
/// <list type="number">
///   <item>If the template at the current position is a "$ref" object (the structured
///         reference form), check if the ref path is secret-tainted; if so, redact the
///         whole resolved value at this position.</item>
///   <item>If the template is a plain object AND the resolved value is also an object,
///         descend per-key — recurse on each matching child. Unmatched resolved keys
///         (synthetic, no template) pass through.</item>
///   <item>Otherwise (template is a string with <c>{{ref}}</c> templates, an array,
///         or any leaf), check ALL referenced paths under it. If any one is secret,
///         redact the whole resolved value at this position.</item>
/// </list>
/// <para>This achieves "smallest containing key" redaction: an HTTP request's
/// headers.Authorization gets redacted but headers.ContentType passes through,
/// preserving debugging value while protecting credentials.</para>
/// </summary>
public sealed class PayloadRedactor : IPayloadRedactor, IScopedDependency
{
    /// <summary>Marker format. Pinned by a test so any reformat is a deliberate decision.</summary>
    private const string MarkerFormat = "[REDACTED: {0}]";

    public IReadOnlyDictionary<string, JsonElement> RedactBag(
        JsonElement originalTemplate,
        IReadOnlyDictionary<string, JsonElement> resolvedBag,
        IReadOnlySet<string> secretPaths)
    {
        if (secretPaths.Count == 0) return resolvedBag;
        if (originalTemplate.ValueKind != JsonValueKind.Object) return resolvedBag;

        var result = new Dictionary<string, JsonElement>(resolvedBag.Count);

        foreach (var (key, resolvedValue) in resolvedBag)
        {
            if (!originalTemplate.TryGetProperty(key, out var keyTemplate))
            {
                // Synthetic key (no matching template) → pass-through. Engine-injected
                // defaults / fallback values land here.
                result[key] = resolvedValue;
                continue;
            }

            result[key] = RedactValue(keyTemplate, resolvedValue, secretPaths);
        }

        return result;
    }

    /// <summary>
    /// Recursive workhorse. At each call, examines the parallel (template, resolved) pair
    /// and decides: descend, redact, or pass-through.
    /// </summary>
    private static JsonElement RedactValue(JsonElement template, JsonElement resolved, IReadOnlySet<string> secretPaths)
    {
        // Case 1: template is a {"$ref": "path"} structured reference. Treat as leaf —
        // either the path is secret-tainted (redact whole resolved value) or it's not
        // (pass-through). We don't recurse INTO a $ref because the resolved value at a
        // $ref position can be of any shape (object, array, scalar) and there's no
        // sub-template to align against.
        if (IsJsonRef(template, out var refPath))
        {
            return secretPaths.Contains(refPath)
                ? Marker(refPath)
                : resolved;
        }

        // Case 2: template is a plain object AND resolved is an object → descend per-key.
        if (template.ValueKind == JsonValueKind.Object && resolved.ValueKind == JsonValueKind.Object)
        {
            using var writer = new System.IO.MemoryStream();
            using (var jw = new Utf8JsonWriter(writer))
            {
                jw.WriteStartObject();
                foreach (var prop in resolved.EnumerateObject())
                {
                    if (template.TryGetProperty(prop.Name, out var subTemplate))
                    {
                        var redactedChild = RedactValue(subTemplate, prop.Value, secretPaths);
                        jw.WritePropertyName(prop.Name);
                        redactedChild.WriteTo(jw);
                    }
                    else
                    {
                        // Synthetic descendant — no template guidance → pass-through.
                        jw.WritePropertyName(prop.Name);
                        prop.Value.WriteTo(jw);
                    }
                }
                jw.WriteEndObject();
            }
            return JsonDocument.Parse(writer.ToArray()).RootElement.Clone();
        }

        // Case 3: leaf level (template is string / number / array / mismatched-shape) or
        // resolved is a scalar even when template is an object (e.g. node emitted a flat
        // string where template declared a structured shape). Redact iff any referenced
        // path under the template is secret-tainted.
        var taintedPath = FindFirstTaintedReference(template, secretPaths);
        return taintedPath is null ? resolved : Marker(taintedPath);
    }

    /// <summary>
    /// True iff <paramref name="element"/> is the structured-reference object
    /// (<c>{"$ref": "some.path"}</c>) — outputs the referenced path. Mirrors the
    /// VariableResolver's $ref detection logic, kept local to avoid exposing internals.
    /// </summary>
    private static bool IsJsonRef(JsonElement element, out string refPath)
    {
        refPath = "";
        if (element.ValueKind != JsonValueKind.Object) return false;

        var props = element.EnumerateObject().ToList();
        if (props.Count != 1 || props[0].Name != JsonRef.PropertyName) return false;
        if (props[0].Value.ValueKind != JsonValueKind.String) return false;

        refPath = props[0].Value.GetString() ?? "";
        return refPath.Length > 0;
    }

    private static string? FindFirstTaintedReference(JsonElement template, IReadOnlySet<string> secretPaths)
    {
        var referenced = VariableResolver.ExtractReferencedPaths(template);
        foreach (var path in referenced)
        {
            if (secretPaths.Contains(path)) return path;
        }
        return null;
    }

    private static JsonElement Marker(string path) =>
        JsonSerializer.SerializeToElement(string.Format(MarkerFormat, path));
}
