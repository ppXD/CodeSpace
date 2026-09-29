using System.Text.Json;
using System.Text.Json.Nodes;

namespace CodeSpace.Core.Services.Workflows.Llm;

/// <summary>
/// Derives a schema's COMBINATOR-FREE form: <c>oneOf</c>, <c>anyOf</c>, <c>allOf</c>, <c>not</c>, <c>if</c>, <c>then</c>
/// and <c>else</c> dropped at every schema position, every other keyword, property name and literal kept verbatim. This
/// is the form a provider's constrained decoder is handed as <see cref="StructuredLLMCompletionRequest.WireJsonSchema"/>
/// while the full schema keeps validating every reply.
///
/// <para>Why it exists: a hosted vLLM backend compiles the forced tool's schema into a decoding grammar, and a
/// combinator shape it cannot compile fails the whole call with an EMPTY HTTP 500 — not a 400 the progressive fallback
/// degrades on. The planner's per-kind acceptance branches did exactly that on every call, while every combinator-free
/// schema sent to the same model was answered. The error names no keyword, so every combinator goes, not a guessed one.</para>
///
/// <para>Why it is safe: each combinator only adds a constraint alongside its siblings, so dropping one can only widen
/// what a schema accepts, and the model is never forbidden a reply the contract allows. What the wire no longer says is
/// still enforced: the bounded re-ask validates against the full schema. The one keyword family this reasoning would
/// not hold for, <c>unevaluatedProperties</c>/<c>unevaluatedItems</c>, reads annotations out of these subschemas; no
/// model-facing schema here uses it.</para>
/// </summary>
public static class JsonSchemaCombinators
{
    private static readonly string[] Keywords = ["oneOf", "anyOf", "allOf", "not", "if", "then", "else"];

    /// <summary>Keywords whose value maps NAMES to subschemas — the names are data (a property called <c>not</c> is still a property), only the values are schemas.</summary>
    private static readonly HashSet<string> NamedSubschemas = ["properties", "patternProperties", "$defs", "definitions", "dependentSchemas"];

    /// <summary>Keywords whose value is a subschema, or an array of them. Anything else (<c>enum</c>, <c>const</c>, <c>default</c>, <c>required</c>, …) is data and is copied untouched.</summary>
    private static readonly HashSet<string> Subschemas = ["items", "prefixItems", "additionalItems", "additionalProperties", "contains", "propertyNames", "unevaluatedItems", "unevaluatedProperties"];

    /// <summary>A copy of <paramref name="schema"/> with every combinator removed; the input is never modified.</summary>
    public static JsonElement Strip(JsonElement schema)
    {
        var copy = JsonNode.Parse(schema.GetRawText());

        StripSchema(copy);

        return JsonSerializer.SerializeToElement(copy);
    }

    private static void StripSchema(JsonNode? node)
    {
        if (node is not JsonObject schema) return;

        foreach (var keyword in Keywords) schema.Remove(keyword);

        foreach (var (keyword, value) in schema) StripChildren(keyword, value);
    }

    private static void StripChildren(string keyword, JsonNode? value)
    {
        if (NamedSubschemas.Contains(keyword) && value is JsonObject named) StripEach(named.Select(pair => pair.Value));
        else if (Subschemas.Contains(keyword) && value is JsonArray list) StripEach(list);
        else if (Subschemas.Contains(keyword)) StripSchema(value);
    }

    private static void StripEach(IEnumerable<JsonNode?> schemas)
    {
        foreach (var schema in schemas) StripSchema(schema);
    }
}
