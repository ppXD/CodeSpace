using CodeSpace.Core.Services.Workflows;
using CodeSpace.Messages.Tasks.Timeline;

namespace CodeSpace.Core.Services.Tasks.Timeline.Sources;

/// <summary>
/// The flow.map PLAN timeline source — reads the run and emits ONE orchestration-beat event per map node's planner
/// ("Planned N subtasks", at the planner's completion), so the journal shows a non-supervisor run's plan the same way it
/// shows a supervisor's PLAN decision, and BEFORE the dispatch beat. Planner + subtasks come from the shared
/// <see cref="MapPlan"/>. Feeds BOTH the Activity timeline and the journal (one ordering authority — no separate phase
/// read). READ-ONLY — a drop-in source the projector fans out automatically.
/// </summary>
public sealed class MapPlannerTimelineSource : IRunTimelineSource
{
    private readonly IWorkflowService _workflows;

    public MapPlannerTimelineSource(IWorkflowService workflows)
    {
        _workflows = workflows;
    }

    public string SourceKey => MapPlannerTimelineMap.Key;

    public async Task<IReadOnlyList<RunTimelineEvent>> ContributeAsync(RunTimelineContext context, CancellationToken cancellationToken)
    {
        var run = await _workflows.GetRunAsync(context.RunId, context.TeamId, cancellationToken).ConfigureAwait(false);

        if (run == null) return Array.Empty<RunTimelineEvent>();

        // A planner that hasn't completed hasn't authored its plan yet — skip it (no anchor time). The anchor is the
        // planner's completion, which precedes the map's start, so the plan beat sorts BEFORE the dispatch beat.
        return MapPlan.PlannersOf(run)
            .Where(p => p.Producer.CompletedAt is not null)
            .Select(p => MapPlannerTimelineMap.ToEvent(p.Producer, p.Subtasks.GetArrayLength(), p.Producer.CompletedAt!.Value))
            .ToList();
    }
}
