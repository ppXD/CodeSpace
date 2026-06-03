using System.Text.Json;
using CodeSpace.Core.Services.Workflows.Nodes;
using CodeSpace.Messages.Dtos.Workflows;

namespace CodeSpace.Core.Services.Workflows.Engine;

/// <summary>
/// Pure derivation of which provider instances a wait's RESPONDER must have a linked identity on,
/// because a downstream act-as-user node (one whose manifest declares <see cref="ActsAsUserSpec"/>) will
/// act AS that responder. Framework-free + DB-free so it unit-tests exhaustively; the wrapping service
/// does the repo→provider-instance + identity lookups.
///
/// Conservative by design: follows every downstream edge regardless of branch/condition (a node that
/// COULD run gates the identity — better to prompt a link than fail mid-run). A provider input that can't
/// be resolved from the run inputs (e.g. it references an upstream node output) is skipped, never guessed.
/// Generic: knows nothing about pr_review — any node that declares the spec is covered.
/// </summary>
public static class ActorIdentityRequirementPlan
{
    public sealed record Requirement
    {
        public required string NodeId { get; init; }
        public required ActorProviderSource ProviderSource { get; init; }

        /// <summary>The resolved id string — a repository id (Repository) or a provider instance id (ProviderInstance).</summary>
        public required string ResolvedId { get; init; }

        /// <summary>The capability the node exercises as the actor — the gate scope-checks the actor's token against
        /// its per-provider requirement. Null = no scope pre-check (identity + membership only).</summary>
        public Type? CapabilityType { get; init; }
    }

    /// <summary>
    /// Every distinct provider requirement contributed by act-as-user nodes downstream of
    /// <paramref name="waitNodeId"/> whose actor input references that wait (so the responder IS the actor).
    /// </summary>
    public static IReadOnlyList<Requirement> Derive(WorkflowDefinition definition, string waitNodeId, Func<string, ActsAsUserSpec?> actsAsUserOf, IReadOnlyDictionary<string, JsonElement> inputScope)
    {
        var downstream = ReachableFrom(definition, waitNodeId);

        var requirements = new List<Requirement>();
        var seen = new HashSet<string>();

        foreach (var node in definition.Nodes)
        {
            if (!downstream.Contains(node.Id)) continue;

            var spec = actsAsUserOf(node.TypeKey);
            if (spec == null || node.Inputs.ValueKind != JsonValueKind.Object) continue;

            // Gate only when THIS wait's responder is the actor — the node's actor input must reference the
            // wait. An act-as-user node bound to a different / static user isn't the responder's concern.
            if (!ReferencesNode(node.Inputs, spec.ActorInputKey, waitNodeId)) continue;

            var resolved = ResolveFromInputs(node.Inputs, spec.ProviderInputKey, inputScope);
            if (resolved == null) continue;

            // Dedup per (provider-target, capability): two nodes acting on the same repo via DIFFERENT
            // capabilities each contribute a requirement so each capability's scope gets checked.
            if (seen.Add($"{spec.ProviderSource}:{resolved}:{spec.CapabilityType?.Name}"))
                requirements.Add(new Requirement { NodeId = node.Id, ProviderSource = spec.ProviderSource, ResolvedId = resolved, CapabilityType = spec.CapabilityType });
        }

        return requirements;
    }

    private static HashSet<string> ReachableFrom(WorkflowDefinition definition, string startNodeId)
    {
        var adjacency = definition.Edges.GroupBy(e => e.From).ToDictionary(g => g.Key, g => g.Select(e => e.To).ToList());

        var visited = new HashSet<string>();
        var queue = new Queue<string>();
        foreach (var to in adjacency.GetValueOrDefault(startNodeId, new List<string>())) queue.Enqueue(to);

        while (queue.Count > 0)
        {
            var id = queue.Dequeue();
            if (!visited.Add(id)) continue;
            foreach (var to in adjacency.GetValueOrDefault(id, new List<string>())) queue.Enqueue(to);
        }

        return visited;
    }

    /// <summary>True when the node's input <paramref name="key"/> is a ref mentioning <paramref name="nodeId"/> (covers both {{…}} and {"$ref":…} via the raw text).</summary>
    private static bool ReferencesNode(JsonElement inputs, string key, string nodeId)
    {
        if (!inputs.TryGetProperty(key, out var value)) return false;

        var raw = value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : value.GetRawText();
        return raw.Contains($"nodes.{nodeId}.", StringComparison.Ordinal);
    }

    /// <summary>Resolve an input to an id string when it's a literal id or a whole-value {{input.X}} / {"$ref":"input.X"} ref into the run input scope. Null otherwise (unresolved / non-input ref).</summary>
    private static string? ResolveFromInputs(JsonElement inputs, string key, IReadOnlyDictionary<string, JsonElement> inputScope)
    {
        if (!inputs.TryGetProperty(key, out var value)) return null;

        if (value.ValueKind == JsonValueKind.String)
        {
            var s = value.GetString() ?? "";
            var inputName = WholeInputRef(s);

            if (inputName != null) return ReadStringInput(inputScope, inputName);

            return s.Contains("{{", StringComparison.Ordinal) ? null : s;   // a literal id, not an unresolved ref
        }

        if (value.ValueKind == JsonValueKind.Object && value.TryGetProperty("$ref", out var refEl) && refEl.ValueKind == JsonValueKind.String)
        {
            var path = refEl.GetString() ?? "";
            if (path.StartsWith("input.", StringComparison.Ordinal)) return ReadStringInput(inputScope, path["input.".Length..]);
        }

        return null;
    }

    private static string? ReadStringInput(IReadOnlyDictionary<string, JsonElement> inputScope, string name) =>
        inputScope.TryGetValue(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    /// <summary>Returns "X" when <paramref name="s"/> is exactly "{{input.X}}" (whitespace-tolerant, no filters/spaces), else null.</summary>
    private static string? WholeInputRef(string s)
    {
        var t = s.Trim();
        if (!t.StartsWith("{{", StringComparison.Ordinal) || !t.EndsWith("}}", StringComparison.Ordinal)) return null;

        var inner = t[2..^2].Trim();
        return inner.StartsWith("input.", StringComparison.Ordinal) && !inner.Contains(' ') ? inner["input.".Length..] : null;
    }
}
