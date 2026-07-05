using CodeSpace.Messages.Dtos.Workflows;
using CodeSpace.Messages.Enums;
using CodeSpace.Messages.Tasks.Timeline;

namespace CodeSpace.Core.Services.Tasks.Timeline.Sources;

/// <summary>
/// Projects a flow.map run's PLANNER completion into ONE orchestration-beat timeline event — a generic "Planned N
/// subtasks" moment at the planner node's completion, so a non-supervisor (flow.map) run surfaces its plan beat BEFORE
/// its dispatch beat (the plan → dispatch → agents spine), mirroring a supervisor's PLAN → DISPATCH. A planner that
/// FAILED reads "Planning failed" at Error tone with its node error (never a bland "Planned 0 subtasks" green), so a
/// non-supervisor run's plan-time failure is as legible as a supervisor's. Pure mapping; the source resolves the
/// planner + its subtasks via <c>MapPlan</c>.
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
        Title = TitleFor(plannerNode, subtaskCount),
        Summary = plannerNode.Status == NodeStatus.Failure ? plannerNode.Error : null,
        Severity = SeverityFor(plannerNode.Status),
        Level = TimelineLevel.Milestone,   // an orchestration beat — it shows in the ③ timeline, never folds
        OccurredAt = at,
        Order = 0,
        NodeId = plannerNode.NodeId,
        SourceKey = Key,
    };

    /// <summary>A failed planner reads "Planning failed" (its error rides the summary); a planner that produced no subtask says so; otherwise the subtask count.</summary>
    private static string TitleFor(WorkflowRunNodeSummary plannerNode, int subtaskCount)
    {
        if (plannerNode.Status == NodeStatus.Failure) return "Planning failed";

        return subtaskCount == 0 ? "Planned no subtasks" : $"Planned {subtaskCount} subtask{(subtaskCount == 1 ? "" : "s")}";
    }

    private static TimelineSeverity SeverityFor(NodeStatus status) => status == NodeStatus.Failure ? TimelineSeverity.Error : TimelineSeverity.Info;
}
