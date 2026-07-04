using CodeSpace.Core.Services.Workflows;
using CodeSpace.Messages.Tasks.Timeline;

namespace CodeSpace.Core.Services.Tasks.Timeline.Sources;

/// <summary>
/// The flow.map DISPATCH timeline source — reads the run's node summaries and emits ONE orchestration-beat event per map
/// node that fanned out ("dispatched N agents", at the map's start), so the journal shows a non-supervisor run's dispatch
/// the same way it shows a supervisor spawn. Membership + branches come from the shared <see cref="MapFanout"/>. Feeds
/// BOTH the Activity timeline and the journal (one ordering authority — no separate phase read). READ-ONLY.
/// </summary>
public sealed class MapDispatchTimelineSource : IRunTimelineSource
{
    private readonly IWorkflowService _workflows;

    public MapDispatchTimelineSource(IWorkflowService workflows)
    {
        _workflows = workflows;
    }

    public string SourceKey => MapDispatchTimelineMap.Key;

    public async Task<IReadOnlyList<RunTimelineEvent>> ContributeAsync(RunTimelineContext context, CancellationToken cancellationToken)
    {
        var run = await _workflows.GetRunAsync(context.RunId, context.TeamId, cancellationToken).ConfigureAwait(false);

        if (run == null) return Array.Empty<RunTimelineEvent>();

        // A map that never started never dispatched — skip it (no anchor time). The agent count is the branches that
        // actually staged an agent (the fan-out width), matching the cards the facts source folds onto the beat.
        return MapFanout.MapNodesOf(run.Nodes)
            .Where(m => (m.Node.StartedAt ?? m.Node.CompletedAt) is not null)
            .Select(m => MapDispatchTimelineMap.ToEvent(
                m.Node,
                m.Branches.Count(b => !string.IsNullOrEmpty(b.AgentRunId)),
                (m.Node.StartedAt ?? m.Node.CompletedAt)!.Value))
            .ToList();
    }
}
