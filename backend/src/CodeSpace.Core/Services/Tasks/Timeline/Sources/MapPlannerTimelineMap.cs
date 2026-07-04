using CodeSpace.Messages.Dtos.Workflows;
using CodeSpace.Messages.Tasks.Timeline;

namespace CodeSpace.Core.Services.Tasks.Timeline.Sources;

/// <summary>
/// Projects a flow.map run's PLANNER completion into ONE orchestration-beat timeline event — a generic "Planned N
/// subtasks" moment at the planner node's completion, so a non-supervisor (flow.map) run surfaces its plan beat BEFORE
/// its dispatch beat (the plan → dispatch → agents spine), mirroring a supervisor's PLAN → DISPATCH. Pure mapping; the
/// source resolves the planner + its subtasks via <c>MapPlan</c>.
/// </summary>
public static class MapPlannerTimelineMap
{
    /// <summary>This source's provenance key — the describer + the plan facts source both match on it.</summary>
    public const string Key = "map-plan";

    /// <summary>The event kind, distinct from the run-record <c>node.*</c> lifecycle kinds so the map-plan describer never races the lifecycle describer.</summary>
    public const string PlanKind = "map.plan";

    /// <summary>Stable event id keyed by the planner node — the SAME id the plan facts source keys its subtasks by.</summary>
    public static string EventId(string plannerNodeId) => $"map-plan-{plannerNodeId}";

    public static RunTimelineEvent ToEvent(WorkflowRunNodeSummary plannerNode, int subtaskCount, DateTimeOffset at) => new()
    {
        Id = EventId(plannerNode.NodeId),
        Kind = PlanKind,
        Title = $"Planned {subtaskCount} subtask{(subtaskCount == 1 ? "" : "s")}",
        Severity = TimelineSeverity.Info,
        Level = TimelineLevel.Milestone,   // an orchestration beat — it shows in the ③ timeline, never folds
        OccurredAt = at,
        Order = 0,
        NodeId = plannerNode.NodeId,
        SourceKey = Key,
    };
}
