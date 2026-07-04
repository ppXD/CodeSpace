using System.Text.Json;
using System.Text.RegularExpressions;
using CodeSpace.Messages.Dtos.Workflows;

namespace CodeSpace.Core.Services.Workflows;

/// <summary>
/// Pure resolution of a flow.map run's PLANNER — the node whose authored subtasks the map fans out over, and the plan it
/// produced. A map binds its elements from a node output (<c>items = "{{nodes.&lt;id&gt;.outputs.json.subtasks}}"</c> — the
/// planner directly when ungated, the confirm gate's approved version when gated); this reads that binding out of the
/// run's version-pinned definition, finds the producing top-level node, and exposes the subtasks array it authored.
/// Builder-AGNOSTIC: it keys off the map's OWN items binding, never a hardcoded <c>"planner"</c>/<c>"confirm"</c> node id,
/// so any recipe whose map fans out a node's subtasks surfaces its plan. Shared by the map-plan beat (its timeline source
/// + its plan facts source), mirroring how <see cref="MapFanout"/> is shared. Pure over the run detail — no DB, no drift.
/// </summary>
public static class MapPlan
{
    /// <summary>Pulls the producing node id out of a <c>{{nodes.&lt;id&gt;.outputs...}}</c> binding.</summary>
    private static readonly Regex NodeRef = new(@"\{\{\s*nodes\.([A-Za-z0-9_-]+)\.", RegexOptions.Compiled);

    /// <summary>Each of the run's map nodes paired with the planner that authored its elements + that planner's subtasks array. A map whose items don't bind a node output, whose producer isn't a found top-level node, or that authored no subtasks contributes nothing.</summary>
    public static IReadOnlyList<MapPlanner> PlannersOf(WorkflowRunDetail run)
    {
        if (run.Definition is not { } definition) return Array.Empty<MapPlanner>();

        var planners = new List<MapPlanner>();

        foreach (var map in MapFanout.MapNodesOf(run.Nodes))
        {
            var producerId = ResolveProducerId(definition, map.Node.NodeId);

            if (producerId is null) continue;

            var producer = run.Nodes.FirstOrDefault(n => string.IsNullOrEmpty(n.IterationKey) && n.NodeId == producerId);

            if (producer is null || !TryReadSubtasks(producer.Outputs, out var subtasks) || subtasks.GetArrayLength() == 0) continue;

            planners.Add(new MapPlanner(producer, subtasks));
        }

        return planners;
    }

    /// <summary>The node id the map's <c>items</c> bind their subtasks from — null when items are a literal array or a non-node binding.</summary>
    private static string? ResolveProducerId(WorkflowDefinition definition, string mapNodeId)
    {
        var mapNode = definition.Nodes.FirstOrDefault(n => n.Id == mapNodeId);

        if (mapNode is null || mapNode.Inputs.ValueKind != JsonValueKind.Object
            || !mapNode.Inputs.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.String)
            return null;

        var match = NodeRef.Match(items.GetString() ?? string.Empty);

        return match.Success ? match.Groups[1].Value : null;
    }

    /// <summary>The producer's authored subtasks — <c>outputs.json.subtasks</c> (the exact array the map binds) preferred, the typed <c>outputs.items</c> array as a fallback. Both carry a camelCase <c>id</c> + <c>title</c> per subtask.</summary>
    private static bool TryReadSubtasks(JsonElement outputs, out JsonElement subtasks)
    {
        subtasks = default;

        if (outputs.ValueKind != JsonValueKind.Object) return false;

        if (outputs.TryGetProperty("json", out var json) && json.ValueKind == JsonValueKind.Object
            && json.TryGetProperty("subtasks", out var fromJson) && fromJson.ValueKind == JsonValueKind.Array)
        {
            subtasks = fromJson;
            return true;
        }

        if (outputs.TryGetProperty("items", out var fromItems) && fromItems.ValueKind == JsonValueKind.Array)
        {
            subtasks = fromItems;
            return true;
        }

        return false;
    }

    /// <summary>A map's producing node (planner / confirm gate) paired with the subtasks array it authored.</summary>
    public sealed record MapPlanner(WorkflowRunNodeSummary Producer, JsonElement Subtasks);
}
