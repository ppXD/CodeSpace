using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Messages.Decisions;
using CodeSpace.Messages.Tasks.Timeline;
using Microsoft.EntityFrameworkCore;

namespace CodeSpace.Core.Services.Tasks.Timeline.Sources;

/// <summary>
/// The TOOL-CALL timeline source — it reads the side-effecting tool-call ledger (<c>tool_call_ledger</c>) for the run's
/// agent runs and projects each real side effect (a git.open_pr, a git.commit, a governed command) into a timeline
/// event tagged with its agent (and node). The agent runs are read TEAM-SCOPED (mirroring
/// <see cref="AgentEventTimelineSource"/>); the ledger read is team-scoped too (the row carries <c>TeamId</c>) and
/// EXCLUDES the cross-grain <c>decision.request</c> rows — those are DECISIONS the "Needs decision" queue surfaces,
/// not tool executions. The exclusion keys on <c>ToolKind == DecisionToolKinds.DecisionRequest</c> (the canonical
/// discriminator every other ledger consumer uses — both reapers, the decision queue, the answer resolver), NOT on the
/// <c>DecisionEnvelopeJson</c> being set: a decision row is INSERTed with a null envelope and only stashes it AFTER a
/// second write, so an envelope-null proxy would leak a phantom "Called decision.request" during that window (or forever,
/// after a crash between park and stash). Contributes nothing for a run whose agents made no side-effecting call
/// (a read-only / plain workflow run). READ-ONLY.
/// </summary>
public sealed class ToolCallTimelineSource : IRunTimelineSource, IScopedDependency
{
    private readonly CodeSpaceDbContext _db;

    public ToolCallTimelineSource(CodeSpaceDbContext db) { _db = db; }

    public string SourceKey => ToolCallTimelineMap.Key;

    public async Task<IReadOnlyList<RunTimelineEvent>> ContributeAsync(RunTimelineContext context, CancellationToken cancellationToken)
    {
        var nodeByAgent = await LoadRunAgentsAsync(context, cancellationToken).ConfigureAwait(false);

        if (nodeByAgent.Count == 0) return Array.Empty<RunTimelineEvent>();

        var calls = await LoadToolCallsAsync(context.TeamId, nodeByAgent.Keys.ToList(), cancellationToken).ConfigureAwait(false);

        return calls.Select(c => ToolCallTimelineMap.ToEvent(c, nodeByAgent)).ToList();
    }

    /// <summary>The run's agent runs (id → its node), read team-scoped. The run is already team-checked by the projector; the extra TeamId filter is defense in depth, matching the phase + agent-event sources.</summary>
    private async Task<Dictionary<Guid, string?>> LoadRunAgentsAsync(RunTimelineContext context, CancellationToken cancellationToken) =>
        (await _db.AgentRun.AsNoTracking()
            .Where(r => r.TeamId == context.TeamId && r.WorkflowRunId == context.RunId)
            .Select(r => new { r.Id, r.NodeId })
            .ToListAsync(cancellationToken).ConfigureAwait(false))
        .ToDictionary(r => r.Id, r => r.NodeId);

    /// <summary>The run's SIDE-EFFECTING tool calls (a <c>decision.request</c> row is a cross-grain decision, not a tool execution — excluded by ToolKind, the discriminator every other ledger consumer uses), team-scoped, in chronological (CreatedDate) order.</summary>
    private async Task<List<ToolCallLedger>> LoadToolCallsAsync(Guid teamId, List<Guid> agentRunIds, CancellationToken cancellationToken) =>
        await _db.ToolCallLedger.AsNoTracking()
            .Where(t => t.TeamId == teamId && agentRunIds.Contains(t.AgentRunId) && t.ToolKind != DecisionToolKinds.DecisionRequest)
            .OrderBy(t => t.CreatedDate)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
}
