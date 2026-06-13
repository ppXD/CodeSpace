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
    // Matches {{ path.to.value }} with arbitrary whitespace around the path. Multiple placeholders
    // per string are independent matches. The path grammar is dotted segments — each a name that may
    // carry zero+ array indices — so {{items[0]}}, {{matrix[0][1]}}, {{nodes.x.outputs.files[2].name}}
    // and {{subtasks.length}} all match. Indices MUST be balanced [digits]: an unbalanced "{{a[0}}" or
    // a stray "{{a]0}}" does NOT match and stays literal text (the head still anchors on [a-zA-Z_], so a
    // leading digit / bracket is rejected). DefinitionValidator reuses THIS regex (internal) so the
    // save-time and run-time grammars can never drift.
    internal static readonly Regex TemplatePattern =
        new(@"\{\{\s*([a-zA-Z_][a-zA-Z0-9_]*(?:\[\d+\])*(?:\.[a-zA-Z0-9_]+(?:\[\d+\])*)*)\s*\}\}", RegexOptions.Compiled);

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

        var headSeg = ParseSegment(segments[0]);
        var root = headSeg.Name;                    // name only — the bucket / node-id / iteration key
        var rest = segments.Skip(1).ToArray();

        var explicitResult = root switch
        {
            "trigger" => WalkDictionary(scope.Trigger, rest),
            "team"    => WalkDictionary(scope.Team, rest),
            "wf"      => WalkDictionary(scope.Wf, rest),
            "input"   => WalkDictionary(scope.Input, rest),
            "sys"     => WalkDictionary(scope.Sys, rest),
            "nodes"   => WalkNodesScope(scope, rest),
            "project" => WalkProjectsScope(scope, rest),
            "loop"    => scope.Loop != null ? WalkDictionary(scope.Loop, rest) : null,
            _ => (JsonElement?)null
        };

        if (explicitResult.HasValue) return explicitResult;

        // Implicit iteration scope — only consulted when the head doesn't match a known
        // explicit bucket. This is what makes {{item}} / {{index}} work inside an iterate
        // node WITHOUT needing the operator to write {{iteration.item}}.
        if (scope.Iteration != null && root is not "trigger" and not "team" and not "nodes" and not "wf" and not "input" and not "sys" and not "project" and not "loop")
        {
            if (scope.Iteration.TryGetValue(root, out var iterValue))
            {
                // A bare iteration head may carry its own indices ({{items[0]}}, {{item.tags[0]}}).
                var indexed = ApplyIndices(iterValue, headSeg.Indices);
                if (!indexed.HasValue) return null;

                return rest.Length == 0 ? indexed : WalkElement(indexed.Value, rest);
            }
        }

        return null;
    }

    /// <summary>
    /// Walks <c>project.{slug}.{name}</c> against <see cref="NodeRunScope.Projects"/>.
    /// The bag is two-level: slug → name → JsonElement. Anything beyond <c>{slug}.{name}</c>
    /// further descends into the value (e.g. <c>project.team.dataset.fields[0]</c>).
    /// </summary>
    private static JsonElement? WalkProjectsScope(NodeRunScope scope, string[] segments)
    {
        if (segments.Length < 2) return null;   // need at least slug + name

        var slug = segments[0];
        if (!scope.Projects.TryGetValue(slug, out var bag)) return null;

        return WalkDictionary(bag, segments.Skip(1).ToArray());
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

        // The first segment is a dict key that may itself carry indices (a node output named 'files'
        // referenced as 'files[2]'). Look up the name, then apply the indices to the value.
        var head = ParseSegment(segments[0]);
        if (!dict.TryGetValue(head.Name, out var value)) return null;

        var indexed = ApplyIndices(value, head.Indices);
        if (!indexed.HasValue) return null;

        return WalkElement(indexed.Value, segments.Skip(1).ToArray());
    }

    private static JsonElement? WalkElement(JsonElement element, string[] segments)
    {
        var current = element;

        for (var s = 0; s < segments.Length; s++)
        {
            var seg = ParseSegment(segments[s]);
            var isLast = s == segments.Length - 1;

            // `.length` pseudo-property: only as the final segment, with no own index, and only on an
            // Array (the headline {{…subtasks.length}} for flow.map). An Object that genuinely has a
            // "length" key never reaches here as that kind — it's matched by TryGetProperty below — so
            // a real property always wins; arrays can't carry a real member, so there's no ambiguity.
            if (isLast && seg.Indices.Length == 0 && seg.Name == "length" && current.ValueKind == JsonValueKind.Array)
                return JsonSerializer.SerializeToElement(current.GetArrayLength());

            if (current.ValueKind != JsonValueKind.Object) return null;
            if (!current.TryGetProperty(seg.Name, out var next)) return null;

            var indexed = ApplyIndices(next, seg.Indices);
            if (!indexed.HasValue) return null;

            current = indexed.Value;
        }

        return current;
    }

    /// <summary>One dotted-path segment, split into its property name and any trailing array indices.</summary>
    private readonly record struct Seg(string Name, int[] Indices);

    private static readonly int[] NoIndices = Array.Empty<int>();

    /// <summary>
    /// Parses one segment into (name, indices): <c>files</c> → (files, []); <c>files[2]</c> → (files, [2]);
    /// <c>m[0][1]</c> → (m, [0,1]). The inline-template regex guarantees balanced <c>[digits]</c>, but a
    /// raw <c>$ref</c> string is unchecked — so a malformed segment (unbalanced bracket, non-digit index,
    /// trailing junk, overflowing index) is treated as a single literal property name, which resolves to a
    /// clean miss (null) just like any other unknown key. No throw, ever.
    /// </summary>
    private static Seg ParseSegment(string raw)
    {
        var bracket = raw.IndexOf('[');
        if (bracket < 0) return new Seg(raw, NoIndices);

        var name = raw[..bracket];
        var indices = new List<int>();
        var i = bracket;

        while (i < raw.Length && raw[i] == '[')
        {
            var close = raw.IndexOf(']', i);
            if (close < 0) return new Seg(raw, NoIndices);

            var inner = raw[(i + 1)..close];
            if (inner.Length == 0 || !inner.All(char.IsAsciiDigit) || !int.TryParse(inner, out var index))
                return new Seg(raw, NoIndices);

            indices.Add(index);
            i = close + 1;
        }

        if (i != raw.Length) return new Seg(raw, NoIndices);   // trailing junk after the indices

        return new Seg(name, indices.ToArray());
    }

    /// <summary>Applies array indices in order. Non-array / out-of-range / negative → null (a normal miss).</summary>
    private static JsonElement? ApplyIndices(JsonElement element, int[] indices)
    {
        var current = element;

        foreach (var index in indices)
        {
            if (current.ValueKind != JsonValueKind.Array) return null;
            if (index < 0 || index >= current.GetArrayLength()) return null;

            current = current[index];
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
    /// <para>Returned paths are normalized to their TAINTABLE BASE — array indices and a trailing
    /// <c>.length</c> are stripped — so a reference into part of a value (<c>team.SECRET[0]</c>,
    /// <c>team.SECRET.length</c>) still string-equals the secret's base path (<c>team.SECRET</c>).
    /// Without this, an indexed/length reference would slip past the engine's exact-match secret guard.
    /// Plain dotted paths are returned verbatim (the strip is a no-op).</para>
    ///
    /// <para>Used by the engine's Terminal-output secret-leak guard and the payload redactor, which both
    /// compare the returned paths against the secret-paths set by string equality.</para>
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
                    sink.Add(ToTaintableBase(refPath));
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
                foreach (Match match in TemplatePattern.Matches(raw)) sink.Add(ToTaintableBase(match.Groups[1].Value));
                return;
        }
    }

    /// <summary>
    /// Strips accessor suffixes so a reference resolves to the data path the secret guard knows: each
    /// segment's array indices are dropped (<c>SECRET[0]</c> → <c>SECRET</c>), and a single trailing
    /// <c>length</c> pseudo-segment is dropped (<c>SECRET.length</c> → <c>SECRET</c>). A <c>length</c>
    /// that is NOT the last segment is a real object key and is preserved.
    ///
    /// <para>Tradeoff (intentional): this slightly OVER-taints — the <c>.length</c> of a secret array is
    /// a count, not the secret content, yet it's treated as touching the secret. Over-tainting is the
    /// leak-safe default; under-tainting could expose a secret element.</para>
    /// </summary>
    private static string ToTaintableBase(string path)
    {
        var segments = path.Split('.');

        for (var i = 0; i < segments.Length; i++)
        {
            var bracket = segments[i].IndexOf('[');
            if (bracket >= 0) segments[i] = segments[i][..bracket];
        }

        var end = segments.Length;
        if (end > 1 && segments[end - 1] == "length") end--;

        return string.Join('.', segments[..end]);
    }
}
