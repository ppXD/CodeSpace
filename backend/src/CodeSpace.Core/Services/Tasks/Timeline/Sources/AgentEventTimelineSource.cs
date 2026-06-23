using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Messages.Tasks.Timeline;
using Microsoft.EntityFrameworkCore;

namespace CodeSpace.Core.Services.Tasks.Timeline.Sources;

/// <summary>
/// The AGENT-EVENTS timeline source — it reads the harness event log (<c>agent_run_event</c>) for the run's agent
/// runs and projects the NARRATIVE-worthy events (file edits, test output, errors/warnings, the final summary) into
/// timeline events, each tagged with its agent (and node). The agent runs are read TEAM-SCOPED (mirroring
/// WorkflowNodePhaseSource — defense in depth on top of the projector's run precheck), and the event read is filtered
/// to <see cref="AgentEventTimelineMap.Narrative"/> kinds in SQL so a chatty run never floods the story line.
/// Contributes nothing for a run with no agents (a plain workflow). READ-ONLY.
/// </summary>
public sealed class AgentEventTimelineSource : IRunTimelineSource, IScopedDependency
{
    private readonly CodeSpaceDbContext _db;

    public AgentEventTimelineSource(CodeSpaceDbContext db) { _db = db; }

    public string SourceKey => AgentEventTimelineMap.Key;

    public async Task<IReadOnlyList<RunTimelineEvent>> ContributeAsync(RunTimelineContext context, CancellationToken cancellationToken)
    {
        var nodeByAgent = await LoadRunAgentsAsync(context, cancellationToken).ConfigureAwait(false);

        if (nodeByAgent.Count == 0) return Array.Empty<RunTimelineEvent>();

        var events = await LoadNarrativeEventsAsync(nodeByAgent.Keys.ToList(), cancellationToken).ConfigureAwait(false);

        return events.Select(e => AgentEventTimelineMap.ToEvent(e, nodeByAgent)).ToList();
    }

    /// <summary>The run's agent runs (id → its node), read team-scoped. The run is already team-checked by the projector; the extra TeamId filter is defense in depth, matching the phase source.</summary>
    private async Task<Dictionary<Guid, string?>> LoadRunAgentsAsync(RunTimelineContext context, CancellationToken cancellationToken) =>
        (await _db.AgentRun.AsNoTracking()
            .Where(r => r.TeamId == context.TeamId && r.WorkflowRunId == context.RunId)
            .Select(r => new { r.Id, r.NodeId })
            .ToListAsync(cancellationToken).ConfigureAwait(false))
        .ToDictionary(r => r.Id, r => r.NodeId);

    /// <summary>The narrative-kind events for the run's agents, in chronological order. The kind filter runs in SQL (kind IN …) so verbose reasoning/tool chatter never loads.</summary>
    private async Task<List<AgentRunEvent>> LoadNarrativeEventsAsync(List<Guid> agentRunIds, CancellationToken cancellationToken) =>
        await _db.AgentRunEvent.AsNoTracking()
            .Where(e => agentRunIds.Contains(e.AgentRunId) && AgentEventTimelineMap.Narrative.Contains(e.Kind))
            .OrderBy(e => e.OccurredAt)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
}
