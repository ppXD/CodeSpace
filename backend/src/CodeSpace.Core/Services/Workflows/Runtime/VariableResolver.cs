using System.Text.Json;
using System.Text.RegularExpressions;
using CodeSpace.Messages.Dtos.Workflows;

namespace CodeSpace.Core.Services.Workflows.Runtime;

/// <summary>
/// Resolves design-time NodeDefinition values into run-time concrete values by walking
/// references against the engine's <see cref="NodeRunScope"/>.
///
/// Two reference forms are supported, and ONLY two:
///
///   1. <c>{{ trigger.title }}</c> — inline template inside any string. Multiple
///      placeholders may appear in one string; each is substituted with the scope's
///      stringified value. Used for scalar interpolation: prompts, URLs, comment bodies.
///
///   2. <c>{ "$ref": "nodes.fetch_diff.outputs.files" }</c> — explicit reference object
///      that resolves to the WHOLE referenced value (object, array, scalar). Used when
///      the node needs structured data, not a string. Detected by "object with exactly
///      one property named $ref".
///
/// Paths are dotted, walking the scope: <c>trigger.*</c>, <c>nodes.&lt;id&gt;.outputs.*</c>,
/// <c>env.*</c>. Unknown paths resolve to JSON null — the schema validator on the receiving
/// node catches required-but-null violations.
///
/// This class is intentionally a static utility (Rule 8 / minimal-functional spirit). No
/// state, no DI, easy to unit-test against fixture scopes.
/// </summary>
public static class VariableResolver
{
    // Matches {{ path.to.value }} with arbitrary whitespace around the dotted path.
    // Multiple placeholders per string are independent matches.
    private static readonly Regex TemplatePattern = new(@"\{\{\s*([a-zA-Z_][a-zA-Z0-9_.]*)\s*\}\}", RegexOptions.Compiled);

    /// <summary>
    /// Walk a JsonElement tree, replacing every {{ref}} template inside strings AND every
    /// {"$ref":"..."} object with its resolved value. The output is a fully-realised
    /// JsonElement the node can read directly.
    /// </summary>
    public static JsonElement Resolve(JsonElement source, NodeRunScope scope)
    {
        var resolved = ResolveValue(source, scope);
        var json = JsonSerializer.Serialize(resolved);
        return JsonDocument.Parse(json).RootElement.Clone();
    }

    /// <summary>Bag-of-named-values resolution. Used to resolve a NodeDefinition's Inputs/Config dictionary.</summary>
    public static IReadOnlyDictionary<string, JsonElement> ResolveBag(JsonElement source, NodeRunScope scope)
    {
        if (source.ValueKind != JsonValueKind.Object)
            return new Dictionary<string, JsonElement>();

        var resolved = new Dictionary<string, JsonElement>();

        foreach (var prop in source.EnumerateObject())
        {
            resolved[prop.Name] = Resolve(prop.Value, scope);
        }

        return resolved;
    }

    private static object? ResolveValue(JsonElement element, NodeRunScope scope)
    {
        return element.ValueKind switch
        {
            JsonValueKind.Object => ResolveObject(element, scope),
            JsonValueKind.Array => ResolveArray(element, scope),
            JsonValueKind.String => ResolveString(element.GetString()!, scope),
            JsonValueKind.Number => element.TryGetInt64(out var l) ? l : (object)element.GetDouble(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => null
        };
    }

    private static object? ResolveObject(JsonElement element, NodeRunScope scope)
    {
        // $ref short-circuit: object with EXACTLY one property named "$ref" → walk the path.
        if (TryGetJsonRefPath(element, out var refPath))
        {
            var resolved = WalkPath(refPath, scope);
            return resolved.HasValue ? JsonElementToClr(resolved.Value) : null;
        }

        var dict = new Dictionary<string, object?>();

        foreach (var prop in element.EnumerateObject())
        {
            dict[prop.Name] = ResolveValue(prop.Value, scope);
        }

        return dict;
    }

    private static object?[] ResolveArray(JsonElement element, NodeRunScope scope) =>
        element.EnumerateArray().Select(e => ResolveValue(e, scope)).ToArray();

    private static object? ResolveString(string raw, NodeRunScope scope)
    {
        // Common case fast-path: no template at all.
        if (!raw.Contains("{{")) return raw;

        // Single-placeholder string with no surrounding text → return the raw value, NOT
        // a stringified copy. This is how a string-typed input field can carry a number,
        // boolean, or object reference via {{ }} without lossy round-trip.
        var sole = TemplatePattern.Match(raw);

        if (sole.Success && sole.Value == raw)
        {
            var path = sole.Groups[1].Value;
            var resolved = WalkPath(path, scope);
            return resolved.HasValue ? JsonElementToClr(resolved.Value) : null;
        }

        // Multi-placeholder or mixed-with-text → stringify every match, keep surrounding text.
        return TemplatePattern.Replace(raw, m =>
        {
            var path = m.Groups[1].Value;
            var resolved = WalkPath(path, scope);

            if (!resolved.HasValue) return "";

            var value = resolved.Value;

            return value.ValueKind switch
            {
                JsonValueKind.String => value.GetString() ?? "",
                JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => value.ToString(),
                JsonValueKind.Null or JsonValueKind.Undefined => "",
                _ => JsonSerializer.Serialize(value)
            };
        });
    }

    private static bool TryGetJsonRefPath(JsonElement element, out string path)
    {
        path = "";

        if (element.ValueKind != JsonValueKind.Object) return false;

        var props = element.EnumerateObject().ToList();

        if (props.Count != 1 || props[0].Name != JsonRef.PropertyName) return false;

        if (props[0].Value.ValueKind != JsonValueKind.String) return false;

        path = props[0].Value.GetString() ?? "";
        return path.Length > 0;
    }

    /// <summary>
    /// Walks a dotted path (e.g. "nodes.fetch.outputs.files") against the scope. Returns null
    /// when any segment is missing. Lookup order:
    ///   1. Explicit head: trigger / env / nodes
    ///   2. Iteration scope (when set by an enclosing flow.iterate) — lets {{item}}, {{index}}
    ///      resolve naturally without a prefix
    /// </summary>
    public static JsonElement? WalkPath(string path, NodeRunScope scope)
    {
        var segments = path.Split('.');

        if (segments.Length == 0) return null;

        var root = segments[0];
        var rest = segments.Skip(1).ToArray();

        var explicitResult = root switch
        {
            "trigger" => WalkDictionary(scope.Trigger, rest),
            "team"    => WalkDictionary(scope.Team, rest),
            "wf"      => WalkDictionary(scope.Wf, rest),
            "input"   => WalkDictionary(scope.Input, rest),
            "sys"     => WalkDictionary(scope.Sys, rest),
            "nodes"   => WalkNodesScope(scope, rest),
            _ => (JsonElement?)null
        };

        if (explicitResult.HasValue) return explicitResult;

        // Implicit iteration scope — only consulted when the head doesn't match a known
        // explicit bucket. This is what makes {{item}} / {{index}} work inside an iterate
        // node WITHOUT needing the operator to write {{iteration.item}}.
        if (scope.Iteration != null && root is not "trigger" and not "team" and not "nodes" and not "wf" and not "input" and not "sys")
        {
            if (scope.Iteration.TryGetValue(root, out var iterValue))
            {
                return rest.Length == 0 ? iterValue : WalkElement(iterValue, rest);
            }
        }

        return null;
    }

    private static JsonElement? WalkNodesScope(NodeRunScope scope, string[] segments)
    {
        if (segments.Length < 2) return null;

        var nodeId = segments[0];
        var bucket = segments[1];   // must be "outputs"

        if (bucket != "outputs") return null;
        if (!scope.Nodes.TryGetValue(nodeId, out var outputs)) return null;

        return WalkDictionary(outputs, segments.Skip(2).ToArray());
    }

    private static JsonElement? WalkDictionary(IReadOnlyDictionary<string, JsonElement> dict, string[] segments)
    {
        if (segments.Length == 0) return null;
        if (!dict.TryGetValue(segments[0], out var value)) return null;

        return WalkElement(value, segments.Skip(1).ToArray());
    }

    private static JsonElement? WalkElement(JsonElement element, string[] segments)
    {
        var current = element;

        foreach (var segment in segments)
        {
            if (current.ValueKind != JsonValueKind.Object) return null;
            if (!current.TryGetProperty(segment, out var next)) return null;

            current = next;
        }

        return current;
    }

    private static object? JsonElementToClr(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.Object => element.EnumerateObject().ToDictionary(p => p.Name, p => JsonElementToClr(p.Value)),
            JsonValueKind.Array => element.EnumerateArray().Select(JsonElementToClr).ToArray(),
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Number => element.TryGetInt64(out var l) ? l : element.GetDouble(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => null
        };
    }

    /// <summary>
    /// Walks a design-time template tree (a NodeDefinition.Inputs / .Config <see cref="JsonElement"/>)
    /// and returns every dotted path that gets referenced — both <c>{{path}}</c> inline templates
    /// and <c>{"$ref":"path"}</c> structured references. Recurses into nested objects + arrays so
    /// references buried inside deeply-structured Inputs trees are still surfaced.
    ///
    /// <para>Returned paths are the strings exactly as written in the template (no normalization),
    /// e.g. <c>"team.API_KEY"</c>, <c>"nodes.fetch.outputs.users"</c>. Caller filters against
    /// the secret-paths set / does whatever check it needs.</para>
    ///
    /// <para>Used by the engine's Terminal-output secret-leak guard. Could be reused by future
    /// save-time validators (e.g. statically checking that a node's inputs only reference paths
    /// that exist at run time).</para>
    /// </summary>
    public static IReadOnlyList<string> ExtractReferencedPaths(JsonElement template)
    {
        var paths = new List<string>();
        CollectReferencedPaths(template, paths);
        return paths;
    }

    private static void CollectReferencedPaths(JsonElement element, List<string> sink)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                if (TryGetJsonRefPath(element, out var refPath))
                {
                    sink.Add(refPath);
                    return;
                }
                foreach (var prop in element.EnumerateObject()) CollectReferencedPaths(prop.Value, sink);
                return;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray()) CollectReferencedPaths(item, sink);
                return;
            case JsonValueKind.String:
                var raw = element.GetString();
                if (string.IsNullOrEmpty(raw) || !raw.Contains("{{")) return;
                foreach (Match match in TemplatePattern.Matches(raw)) sink.Add(match.Groups[1].Value);
                return;
        }
    }
}
