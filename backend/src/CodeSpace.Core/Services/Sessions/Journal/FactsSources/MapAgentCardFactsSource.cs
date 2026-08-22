using CodeSpace.Core.Services.Tasks.Phases;
using CodeSpace.Core.Services.Tasks.Timeline.Sources;
using CodeSpace.Core.Services.Workflows;
using CodeSpace.Core.Services.Workflows.Display;
using CodeSpace.Messages.Dtos.Workflows;

namespace CodeSpace.Core.Services.Sessions.Journal.FactsSources;

/// <summary>
/// Enriches each flow.map DISPATCH beat with the agents its fan-out ran — render-ready cards keyed by the map dispatch
/// event id (<see cref="MapDispatchTimelineMap.EventId"/>, the same id the describer stamps on the beat). Resolves the
/// map's branch agent-run ids via the shared <see cref="MapFanout"/> and folds cards through the SHARED
/// <see cref="AgentMetricsReader"/> + <see cref="AgentCardFactsSource.ToCard"/> — the SAME path the room and the
/// supervisor card use, so a map agent card can't disagree with the room's. A map with no agent branches / an unreadable
/// row contributes nothing (mirrors the supervisor card's skip guard). ONE batched metrics read for the whole run.
/// </summary>
public sealed class MapAgentCardFactsSource : IJournalFactsSource
{
    private readonly IWorkflowRunViewMetadataReader _metadata;
    private readonly AgentMetricsReader _metrics;

    public MapAgentCardFactsSource(IWorkflowRunViewMetadataReader metadata, AgentMetricsReader metrics)
    {
        _metadata = metadata;
        _metrics = metrics;
    }

    public async Task<IReadOnlyDictionary<string, JournalStepFacts>> GatherAsync(Guid runId, Guid teamId, CancellationToken cancellationToken)
    {
        var run = await _metadata.ReadAsync(runId, teamId, WorkflowRunViewScope.LineageMerged, cancellationToken).ConfigureAwait(false);

        if (run == null) return EmptyFacts;
        if (run.CellsAvailability != WorkflowRunViewAvailability.Available || run.LinksAvailability != WorkflowRunViewAvailability.Available
            || run.TopologyAvailability != WorkflowRunViewAvailability.Available) return EmptyFacts;

        var maps = MapFanout.MapNodesOf(run.Cells);

        if (maps.Count == 0) return EmptyFacts;

        var allAgentIds = maps.SelectMany(m => BranchAgentIds(m.Branches)).Distinct().ToList();
        var metrics = await _metrics.ReadForWorkflowRunAsync(teamId, runId, allAgentIds, DateTimeOffset.UtcNow, cancellationToken).ConfigureAwait(false);

        var facts = new Dictionary<string, JournalStepFacts>();

        foreach (var map in maps)
        {
            var cards = BranchAgentIds(map.Branches)
                .Where(metrics.ContainsKey)   // an id whose row isn't the team's / not yet readable is skipped, never fabricated
                .Select(id => AgentCardFactsSource.ToCard(id, metrics[id], allocation: null, compact: null))
                .ToList();

            if (cards.Count > 0)
                facts[MapDispatchTimelineMap.EventId(map.Node.NodeId)] = new JournalStepFacts { Agents = cards };
        }

        return facts;
    }

    private static IEnumerable<Guid> BranchAgentIds(IEnumerable<WorkflowRunCellMetadata> branches) =>
        branches.Where(value => value.AgentRunId is not null).Select(value => value.AgentRunId!.Value);

    private static readonly IReadOnlyDictionary<string, JournalStepFacts> EmptyFacts = new Dictionary<string, JournalStepFacts>();
}
