using CodeSpace.Core.Services.Tasks.Phases;
using CodeSpace.Core.Services.Tasks.Timeline.Sources;
using CodeSpace.Core.Services.Workflows;

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
    private readonly IWorkflowService _workflows;
    private readonly AgentMetricsReader _metrics;

    public MapAgentCardFactsSource(IWorkflowService workflows, AgentMetricsReader metrics)
    {
        _workflows = workflows;
        _metrics = metrics;
    }

    public async Task<IReadOnlyDictionary<string, JournalStepFacts>> GatherAsync(Guid runId, Guid teamId, CancellationToken cancellationToken)
    {
        var run = await _workflows.GetRunAsync(runId, teamId, cancellationToken).ConfigureAwait(false);

        if (run == null) return EmptyFacts;

        var maps = MapFanout.MapNodesOf(run.Nodes);

        if (maps.Count == 0) return EmptyFacts;

        var allAgentIds = maps.SelectMany(m => BranchAgentIds(m.Branches)).Distinct().ToList();
        var metrics = await _metrics.ReadAsync(teamId, allAgentIds, DateTimeOffset.UtcNow, cancellationToken).ConfigureAwait(false);

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

    private static IEnumerable<Guid> BranchAgentIds(IEnumerable<Messages.Dtos.Workflows.WorkflowRunNodeSummary> branches) =>
        branches
            .Where(b => !string.IsNullOrEmpty(b.AgentRunId) && Guid.TryParse(b.AgentRunId, out _))
            .Select(b => Guid.Parse(b.AgentRunId!));

    private static readonly IReadOnlyDictionary<string, JournalStepFacts> EmptyFacts = new Dictionary<string, JournalStepFacts>();
}
