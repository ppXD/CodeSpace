using CodeSpace.Core.Services.Workflows;
using CodeSpace.Core.Services.Workflows.Display;
using CodeSpace.Messages.Dtos.Workflows;
using CodeSpace.Messages.Tasks.Timeline;

namespace CodeSpace.Core.Services.Tasks.Timeline.Sources;

/// <summary>
/// The flow.map PLAN timeline source — reads the run and emits ONE orchestration-beat event per map node's planner
/// ("Planned N subtasks", at the planner's completion), so the journal shows a non-supervisor run's plan the same way it
/// shows a supervisor's PLAN decision, and BEFORE the dispatch beat. Planner + subtasks come from the shared
/// bounded Map-plan observation bundle. Feeds BOTH the Activity timeline and the journal (one ordering authority — no
/// separate full-detail read). READ-ONLY — a drop-in source the projector fans out automatically.
/// </summary>
public sealed class MapPlannerTimelineSource : IRunTimelineSource
{
    private readonly IWorkflowMapPlanObservationBundle _plans;

    public MapPlannerTimelineSource(IWorkflowMapPlanObservationBundle plans)
    {
        _plans = plans;
    }

    public string SourceKey => MapPlannerTimelineMap.Key;

    public async Task<IReadOnlyList<RunTimelineEvent>> ContributeAsync(RunTimelineContext context, CancellationToken cancellationToken)
    {
        var run = await _plans.GetAsync(context.RunId, context.TeamId, cancellationToken).ConfigureAwait(false);

        if (run == null) return Array.Empty<RunTimelineEvent>();
        if (run.Availability != WorkflowRunViewAvailability.Available)
            return new[] { MapPlannerTimelineMap.CoverageEvent(run.Availability, run.AnchorAt) };

        var events = new List<RunTimelineEvent>();
        foreach (var planner in run.Planners.Where(value => value.CompletedAt is not null))
        {
            if (planner.Status == CodeSpace.Messages.Enums.NodeStatus.Failure)
                events.Add(MapPlannerTimelineMap.ToEvent(planner));
            else if (planner.SubtasksState == WorkflowMapPlanLeafState.Exact && planner.SubtasksTotalCount > 0)
                events.Add(MapPlannerTimelineMap.ToEvent(planner));
            else if (planner.SubtasksState is WorkflowMapPlanLeafState.Truncated or WorkflowMapPlanLeafState.Invalid or WorkflowMapPlanLeafState.Unavailable)
                events.Add(MapPlannerTimelineMap.CoverageEvent(planner, planner.CompletedAt!.Value));
        }
        return events;
    }
}
