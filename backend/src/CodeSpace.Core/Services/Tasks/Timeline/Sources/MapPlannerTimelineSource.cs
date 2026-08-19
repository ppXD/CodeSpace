using CodeSpace.Core.Services.Workflows;
using CodeSpace.Core.Services.Workflows.Artifacts;
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
    private readonly IRunNodeOutputInflater _inflater;

    public MapPlannerTimelineSource(IWorkflowService workflows, IRunNodeOutputInflater inflater)
    {
        _workflows = workflows;
        _inflater = inflater;
    }

    public string SourceKey => MapPlannerTimelineMap.Key;

    public async Task<IReadOnlyList<RunTimelineEvent>> ContributeAsync(RunTimelineContext context, CancellationToken cancellationToken)
    {
        var run = await _workflows.GetRunAsync(context.RunId, context.TeamId, cancellationToken).ConfigureAwait(false);

        if (run == null) return Array.Empty<RunTimelineEvent>();

        // A plan big enough to cross the offload threshold lives in the artifact store, with only a ref on the ledger
        // cell — so the subtask count would read as zero off the bare detail. Inflate the map PRODUCER cells (the only
        // ones this source reads) and nothing else.
        var planned = await _inflater.InflateAsync(run, context.TeamId, MapPlan.ProducerNodeIds(run), cancellationToken).ConfigureAwait(false);

        // A planner that hasn't completed hasn't authored its plan yet — skip it (no anchor time). The anchor is the
        // planner's completion, which precedes the map's start, so the plan beat sorts BEFORE the dispatch beat.
        return MapPlan.PlannersOf(planned)
            .Where(p => p.Producer.CompletedAt is not null)
            .Select(p => MapPlannerTimelineMap.ToEvent(p.Producer, p.Subtasks.GetArrayLength(), p.Producer.CompletedAt!.Value))
            .ToList();
    }
}
