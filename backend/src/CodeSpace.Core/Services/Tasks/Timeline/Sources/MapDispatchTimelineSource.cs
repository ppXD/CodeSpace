using CodeSpace.Core.Services.Workflows;
using CodeSpace.Core.Services.Workflows.Display;
using CodeSpace.Messages.Dtos.Workflows;
using CodeSpace.Messages.Tasks.Timeline;

namespace CodeSpace.Core.Services.Tasks.Timeline.Sources;

/// <summary>
/// The flow.map DISPATCH timeline source — reads the bounded body-blind run metadata and emits ONE orchestration-beat event per map
/// node that fanned out ("dispatched N agents", at the map's start), so the journal shows a non-supervisor run's dispatch
/// the same way it shows a supervisor spawn. Membership + branches come from the shared <see cref="MapFanout"/>. Feeds
/// BOTH the Activity timeline and the journal (one ordering authority — no separate phase read). READ-ONLY.
/// </summary>
public sealed class MapDispatchTimelineSource : IRunTimelineSource
{
    private readonly IWorkflowRunViewMetadataReader _metadata;

    public MapDispatchTimelineSource(IWorkflowRunViewMetadataReader metadata)
    {
        _metadata = metadata;
    }

    public string SourceKey => MapDispatchTimelineMap.Key;

    public async Task<IReadOnlyList<RunTimelineEvent>> ContributeAsync(RunTimelineContext context, CancellationToken cancellationToken)
    {
        var run = await _metadata.ReadAsync(context.RunId, context.TeamId, WorkflowRunViewScope.LineageMerged, cancellationToken).ConfigureAwait(false);

        if (run == null) return Array.Empty<RunTimelineEvent>();

        var availability = CoverageAvailability(run);
        if (availability != WorkflowRunViewAvailability.Available)
            return new[] { MapDispatchTimelineMap.CoverageEvent(availability, run.CompletedAt ?? run.StartedAt ?? run.CreatedDate) };

        // A map that never started never dispatched — skip it (no anchor time). The agent count is the branches that
        // actually staged an agent (the fan-out width), matching the cards the facts source folds onto the beat.
        return MapFanout.MapNodesOf(run.Cells)
            .Where(m => (m.Node.StartedAt ?? m.Node.CompletedAt) is not null)
            .Select(m => MapDispatchTimelineMap.ToEvent(
                m.Node,
                m.Branches.Count(b => b.AgentRunId is not null),
                (m.Node.StartedAt ?? m.Node.CompletedAt)!.Value))
            .ToList();
    }

    private static WorkflowRunViewAvailability CoverageAvailability(WorkflowRunViewMetadata run)
    {
        var values = new[] { run.CellsAvailability, run.LinksAvailability, run.TopologyAvailability };
        if (values.Contains(WorkflowRunViewAvailability.Corrupt)) return WorkflowRunViewAvailability.Corrupt;
        if (values.Contains(WorkflowRunViewAvailability.TooLarge)) return WorkflowRunViewAvailability.TooLarge;
        if (values.Contains(WorkflowRunViewAvailability.Unavailable)) return WorkflowRunViewAvailability.Unavailable;
        return values.Contains(WorkflowRunViewAvailability.Truncated) ? WorkflowRunViewAvailability.Truncated : WorkflowRunViewAvailability.Available;
    }
}
