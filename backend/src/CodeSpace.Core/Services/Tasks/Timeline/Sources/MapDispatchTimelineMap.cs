using CodeSpace.Messages.Dtos.Workflows;
using CodeSpace.Messages.Enums;
using CodeSpace.Messages.Tasks.Timeline;

namespace CodeSpace.Core.Services.Tasks.Timeline.Sources;

/// <summary>
/// Projects a flow.map node's fan-out into ONE orchestration-beat timeline event — a generic "dispatched N agents" moment
/// at the map node's start, so a non-supervisor (flow.map) run surfaces its dispatch beat + agent cards in the journal
/// exactly like a supervisor spawn. A fan-out that staged NO branch says so (not a bland "Dispatched 0 agents"), and the
/// beat's tone rides the map node's OWN status (a failed fan-out reads Error, mirroring the supervisor spawn's
/// status-driven tone — the per-agent outcomes ride their own cards). Pure mapping; the source resolves the branches via
/// <c>MapFanout</c>.
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
        Title = agentCount == 0 ? "Dispatched no agents" : $"Dispatched {agentCount} agent{(agentCount == 1 ? "" : "s")}",
        Summary = SummaryFor(mapNode, agentCount),
        Severity = SeverityFor(mapNode.Status),
        Level = TimelineLevel.Milestone,   // an orchestration beat — it shows in the ③ timeline, never folds
        OccurredAt = at,
        Order = 0,
        NodeId = mapNode.NodeId,
        SourceKey = Key,
    };

    /// <summary>
    /// Body-blind dispatch projection. The raw failure error remains on the exact <c>node.failed</c> ledger event and
    /// Trace; this orchestration beat does not reload an unbounded run body merely to duplicate it.
    /// </summary>
    public static RunTimelineEvent ToEvent(WorkflowRunCellMetadata mapNode, int agentCount, DateTimeOffset at) => new()
    {
        Id = EventId(mapNode.NodeId),
        Kind = DispatchKind,
        Title = agentCount == 0 ? "Dispatched no agents" : $"Dispatched {agentCount} agent{(agentCount == 1 ? "" : "s")}",
        Summary = agentCount == 0
            ? "No agent was dispatched — this map fanned out no branch."
            : mapNode.Status == NodeStatus.Failure ? "The map failed; its recorded error remains available on the node.failed timeline event and Trace." : null,
        Severity = SeverityFor(mapNode.Status),
        Level = TimelineLevel.Milestone,
        OccurredAt = at,
        Order = 0,
        NodeId = mapNode.NodeId,
        SourceKey = Key,
    };

    /// <summary>A visible fail-safe when the bounded metadata plane cannot prove a complete map-dispatch fold.</summary>
    public static RunTimelineEvent CoverageEvent(WorkflowRunViewAvailability availability, DateTimeOffset at) => new()
    {
        Id = "map-dispatch-coverage",
        Kind = "observation.coverage",
        Title = availability == WorkflowRunViewAvailability.Truncated
            ? "Map dispatch history partially available"
            : "Map dispatch history unavailable",
        Summary = availability == WorkflowRunViewAvailability.Truncated
            ? "The bounded cell window was exhausted; one or more dispatch events may be omitted."
            : "The bounded cell or topology metadata could not be read safely; no dispatch event was inferred.",
        Severity = TimelineSeverity.Warning,
        Level = TimelineLevel.Milestone,
        OccurredAt = at,
        SourceKey = Key,
    };

    /// <summary>An empty fan-out explains that nothing ran this round; a FAILED fan-out carries the node's error; a normal dispatch carries none (the agent cards ARE the detail).</summary>
    private static string? SummaryFor(WorkflowRunNodeSummary mapNode, int agentCount) =>
        agentCount == 0 ? "No agent was dispatched — this map fanned out no branch." : mapNode.Status == NodeStatus.Failure ? mapNode.Error : null;

    /// <summary>An orchestration beat is neutral Info unless the fan-out node itself FAILED (then Error) — the tone rides the node's own status, never the per-agent outcomes (those ride their cards).</summary>
    private static TimelineSeverity SeverityFor(NodeStatus status) => status == NodeStatus.Failure ? TimelineSeverity.Error : TimelineSeverity.Info;
}
