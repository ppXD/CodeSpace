using CodeSpace.Messages.Dtos.Workflows;

namespace CodeSpace.Core.Services.Workflows;

/// <summary>
/// Pure resolution of a run's flow.map fan-outs from its node summaries — the ONE place "which top-level node is a map
/// container, and which rows are its direct element branches" is decided, shared by the phase board (<c>WorkflowNodePhaseSource</c>)
/// and the Session Journal (the map-dispatch beat + its agent cards). A top-level node (empty <see cref="WorkflowRunNodeSummary.IterationKey"/>)
/// is a map IFF it has direct <c>flow.map</c> branches; a branch carries <c>ContainerKind == "flow.map"</c> and an
/// iteration key <c>"&lt;mapId&gt;#&lt;i&gt;"</c> whose remainder after the <c>"&lt;mapId&gt;#"</c> prefix has NO <c>'/'</c>
/// (a '/' marks a nested grandchild, not this map's direct element). Pure over the summaries — no DB, no drift.
/// </summary>
public static class MapFanout
{
    public const string ContainerKind = "flow.map";

    /// <summary>The run's top-level MAP nodes, each with its direct element branches (only nodes that actually fanned out — a non-map top-level node contributes nothing).</summary>
    public static IReadOnlyList<MapNode> MapNodesOf(IReadOnlyList<WorkflowRunNodeSummary> nodes) =>
        nodes
            .Where(n => string.IsNullOrEmpty(n.IterationKey))
            .Select(n => new MapNode(n, BranchesOf(n.NodeId, nodes)))
            .Where(m => m.Branches.Count > 0)
            .ToList();

    /// <summary>The direct element-branch rows of one map node — carrying <c>ContainerKind == "flow.map"</c> and a direct <c>"&lt;nodeId&gt;#&lt;i&gt;"</c> iteration key, ordered by that key.</summary>
    public static IReadOnlyList<WorkflowRunNodeSummary> BranchesOf(string nodeId, IReadOnlyList<WorkflowRunNodeSummary> allRows)
    {
        var prefix = nodeId + "#";

        return allRows
            .Where(r => r.ContainerKind == ContainerKind && IsDirectBranch(r.IterationKey, prefix))
            .OrderBy(r => r.IterationKey, StringComparer.Ordinal)
            .ToList();
    }

    private static bool IsDirectBranch(string iterationKey, string prefix) =>
        iterationKey.StartsWith(prefix, StringComparison.Ordinal) &&
        iterationKey.AsSpan(prefix.Length).IndexOf('/') < 0;

    /// <summary>A top-level map node paired with its direct element branches.</summary>
    public sealed record MapNode(WorkflowRunNodeSummary Node, IReadOnlyList<WorkflowRunNodeSummary> Branches);
}
