using CodeSpace.Core.Services.Supervisor;
using CodeSpace.Core.Services.Tasks.Phases;
using CodeSpace.Core.Services.Tasks.Timeline.Sources;
using CodeSpace.Messages.Dtos.Sessions.Journal;
using CodeSpace.Messages.Tasks.Phases;

namespace CodeSpace.Core.Services.Sessions.Journal.FactsSources;

/// <summary>
/// Enriches each supervisor decision that STAGED agents (a spawn's fan-out, a retry's re-run) with the agents it launched
/// — render-ready cards keyed by the decision's timeline event id, so the walk hangs them off the same step the supervisor
/// describer produced. Reads each decision's staged agent ids off its outcome (<see cref="SupervisorOutcome.ReadStagedAgentRunIds"/>)
/// and builds cards from the SHARED <see cref="AgentMetricsReader"/> — the same ground-truth numbers the phase board and
/// the room card read, in ONE batched query for the whole run. A decision that staged none contributes nothing; a run's
/// re-spawn waves need no special handling (each wave is its own later decision step carrying its own cards).
/// </summary>
public sealed class AgentCardFactsSource : IJournalFactsSource
{
    private readonly ISupervisorDecisionLog _decisions;
    private readonly AgentMetricsReader _metrics;

    public AgentCardFactsSource(ISupervisorDecisionLog decisions, AgentMetricsReader metrics)
    {
        _decisions = decisions;
        _metrics = metrics;
    }

    public async Task<IReadOnlyDictionary<string, JournalStepFacts>> GatherAsync(Guid runId, Guid teamId, CancellationToken cancellationToken)
    {
        var tape = await _decisions.GetForRunAsync(runId, teamId, cancellationToken).ConfigureAwait(false);

        var stagedByDecision = tape
            .Select(d => (Decision: d, AgentIds: SupervisorOutcome.ReadStagedAgentRunIds(d.OutcomeJson)))
            .Where(x => x.AgentIds.Count > 0)
            .ToList();

        if (stagedByDecision.Count == 0) return EmptyFacts;

        var allAgentIds = stagedByDecision.SelectMany(x => x.AgentIds).Distinct().ToList();
        var metrics = await _metrics.ReadAsync(teamId, allAgentIds, DateTimeOffset.UtcNow, cancellationToken).ConfigureAwait(false);

        var facts = new Dictionary<string, JournalStepFacts>();

        foreach (var (decision, agentIds) in stagedByDecision)
        {
            var cards = agentIds
                .Where(metrics.ContainsKey)   // an id whose row isn't the team's / not yet readable is skipped, never fabricated
                .Select(id => ToCard(id, metrics[id]))
                .ToList();

            if (cards.Count > 0)
                facts[SupervisorDecisionTimelineMap.EventId(decision)] = new JournalStepFacts { Agents = cards };
        }

        return facts;
    }

    /// <summary>Map the shared metrics projection to a journal card — the ground-truth (status · files · tokens · duration · cost), with a neutral label when the task named no goal. Total tokens is null unless the agent reported usage (0 would read as "measured zero").</summary>
    internal static JournalAgentCard ToCard(Guid agentRunId, AgentRunMetrics m) => new()
    {
        AgentRunId = agentRunId,
        Label = string.IsNullOrWhiteSpace(m.Goal) ? "Agent" : m.Goal!.Trim(),
        Status = m.Status,
        Model = m.Model,
        DurationMs = m.DurationMs,
        Tokens = m.InputTokens is null && m.OutputTokens is null ? null : (m.InputTokens ?? 0) + (m.OutputTokens ?? 0),
        ToolCount = m.ToolCount,
        CostUsd = m.CostUsd,
        FilesChanged = m.FilesChanged,
        Files = m.ChangedFileStats,
    };

    private static readonly IReadOnlyDictionary<string, JournalStepFacts> EmptyFacts = new Dictionary<string, JournalStepFacts>();
}
