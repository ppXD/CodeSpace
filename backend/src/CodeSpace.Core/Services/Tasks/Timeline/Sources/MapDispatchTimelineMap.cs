using CodeSpace.Messages.Dtos.Workflows;
using CodeSpace.Messages.Tasks.Timeline;

namespace CodeSpace.Core.Services.Tasks.Timeline.Sources;

/// <summary>
/// Projects a flow.map node's fan-out into ONE orchestration-beat timeline event — a generic "dispatched N agents" moment
/// at the map node's start, so a non-supervisor (flow.map) run surfaces its dispatch beat + agent cards in the journal
/// exactly like a supervisor spawn. Pure mapping; the source resolves the map + its branches via <c>MapFanout</c>.
/// </summary>
public static class MapDispatchTimelineMap
{
    /// <summary>This source's provenance key — the describer + the agent-card facts source both match on it.</summary>
    public const string Key = "map-dispatch";

    /// <summary>The event kind, distinct from the run-record <c>node.*</c> lifecycle kinds so the map describer never races the lifecycle describer.</summary>
    public const string DispatchKind = "map.dispatch";

    /// <summary>Stable event id keyed by the map node — the SAME id the agent-card facts source keys its cards by.</summary>
    public static string EventId(string nodeId) => $"map-dispatch-{nodeId}";

    public static RunTimelineEvent ToEvent(WorkflowRunNodeSummary mapNode, int agentCount, DateTimeOffset at) => new()
    {
        Id = EventId(mapNode.NodeId),
        Kind = DispatchKind,
        Title = $"Dispatched {agentCount} agent{(agentCount == 1 ? "" : "s")}",
        Severity = TimelineSeverity.Info,
        Level = TimelineLevel.Milestone,   // an orchestration beat — it shows in the ③ timeline, never folds
        OccurredAt = at,
        Order = 0,
        NodeId = mapNode.NodeId,
        SourceKey = Key,
    };
}
