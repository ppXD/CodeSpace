using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Services.Decisions;
using CodeSpace.Core.Services.Tasks.Phases;
using CodeSpace.Messages.Dtos.Decisions;
using CodeSpace.Messages.Dtos.Sessions;
using CodeSpace.Messages.Dtos.Sessions.Room;
using CodeSpace.Messages.Tasks.Phases;
using Microsoft.EntityFrameworkCore;

namespace CodeSpace.Core.Services.Sessions.Room;

/// <summary>
/// Default <see cref="IRoomProjector"/>. Reuses the turn skeleton from <see cref="ISessionReadService"/> (one query,
/// goals + latest-attempt run id + status per turn) and enriches ONLY the focused turn with the heavy projections —
/// the phase tree (<see cref="IRunPhaseProjector"/>), the pending decisions, the capability-aware actions, and the
/// change watermark. Non-focused turns are light cards (re-focus = a cheap re-navigation). All copy / order lives in
/// the pure <see cref="RoomNarrative"/>. READ-ONLY.
/// </summary>
public sealed class RoomProjector : IRoomProjector, IScopedDependency
{
    private readonly ISessionReadService _sessions;
    private readonly IRunPhaseProjector _phases;
    private readonly IDecisionQueueService _decisions;
    private readonly IRunActionCapabilityResolver _actions;
    private readonly CodeSpaceDbContext _db;

    public RoomProjector(ISessionReadService sessions, IRunPhaseProjector phases, IDecisionQueueService decisions, IRunActionCapabilityResolver actions, CodeSpaceDbContext db)
    {
        _sessions = sessions;
        _phases = phases;
        _decisions = decisions;
        _actions = actions;
        _db = db;
    }

    public async Task<RoomView?> ProjectByRunAsync(Guid runId, Guid teamId, CancellationToken cancellationToken)
    {
        var detail = await _sessions.GetByRunAsync(runId, teamId, cancellationToken).ConfigureAwait(false);

        return detail == null ? null : await BuildAsync(detail, detail.AnchorTurnIndex, teamId, cancellationToken).ConfigureAwait(false);
    }

    public async Task<RoomView?> ProjectAsync(Guid sessionId, Guid? focusRunId, Guid teamId, CancellationToken cancellationToken)
    {
        var detail = await _sessions.GetDetailAsync(sessionId, teamId, cancellationToken).ConfigureAwait(false);

        if (detail == null) return null;

        var focus = focusRunId is { } fr ? TurnIndexOf(detail, fr) : null;

        return await BuildAsync(detail, focus, teamId, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The turn a run belongs to — its identity, the latest attempt, or any nested attempt. Null when the run isn't a turn here.</summary>
    private static int? TurnIndexOf(SessionDetail detail, Guid runId) =>
        detail.Turns.FirstOrDefault(t => t.TurnRunId == runId || t.RunId == runId || (t.Attempts?.Any(a => a.RunId == runId) ?? false))?.TurnIndex;

    private async Task<RoomView> BuildAsync(SessionDetail detail, int? focusTurnIndex, Guid teamId, CancellationToken cancellationToken)
    {
        var focused = (focusTurnIndex is { } fi ? detail.Turns.FirstOrDefault(t => t.TurnIndex == fi) : null) ?? detail.Turns.LastOrDefault();

        var blocks = new List<RoomBlock>();
        long cursor = 0;

        foreach (var turn in detail.Turns)
        {
            if (turn.UserMessage is { Length: > 0 } message)
                blocks.Add(new UserMessageBlock { Id = $"turn-{turn.TurnIndex}:user", Seq = 0, Text = message, At = turn.CreatedDate });

            var assistant = focused != null && turn.TurnIndex == focused.TurnIndex
                ? await BuildFocusedTurnAsync(turn, teamId, cancellationToken).ConfigureAwait(false)
                : BuildCollapsedTurn(turn);

            cursor = Math.Max(cursor, assistant.Seq);
            blocks.Add(assistant);
        }

        return new RoomView
        {
            SessionId = detail.Id,
            Title = detail.Title,
            Kind = detail.Kind,
            Status = detail.Status,
            Cursor = cursor,
            AnchorBlockId = focused != null ? $"turn-{focused.TurnIndex}" : null,
            Blocks = blocks,
        };
    }

    private async Task<AssistantTurnBlock> BuildFocusedTurnAsync(SessionTurn turn, Guid teamId, CancellationToken cancellationToken)
    {
        var runId = turn.RunId;   // the latest attempt — what's current

        var phases = await _phases.ProjectAsync(runId, teamId, cancellationToken).ConfigureAwait(false) ?? Array.Empty<RunPhase>();
        var watermark = await WatermarkAsync(runId, cancellationToken).ConfigureAwait(false);

        // Skip the pending-decision read entirely in the common case — the turn skeleton already knows whether this
        // run is parked on one (computed over both park backends), so a non-waiting turn pays zero query + zero parse.
        var decisions = turn.HasPendingDecision
            ? await DecisionBlocksAsync(runId, teamId, watermark, cancellationToken).ConfigureAwait(false)
            : Array.Empty<DecisionBlock>();

        var narrative = RoomNarrative.Build($"turn-{turn.TurnIndex}", watermark, phases, turn.RunStatus, turn.Error, decisions);

        return new AssistantTurnBlock
        {
            Id = $"turn-{turn.TurnIndex}",
            Seq = watermark,
            TurnIndex = turn.TurnIndex,
            TurnRunId = turn.TurnRunId,
            RunId = runId,
            Status = turn.RunStatus,
            Summary = narrative.Summary,
            Map = narrative.Map,
            Blocks = narrative.Blocks,
            Actions = _actions.ResolveTurnActions(runId, turn.RunStatus),
            At = turn.CreatedDate,
        };
    }

    /// <summary>A light card for a non-focused turn — summary + status + actions, no map / inner blocks (the frontend re-focuses by navigating to the run).</summary>
    private AssistantTurnBlock BuildCollapsedTurn(SessionTurn turn) => new()
    {
        Id = $"turn-{turn.TurnIndex}",
        Seq = 0,
        TurnIndex = turn.TurnIndex,
        TurnRunId = turn.TurnRunId,
        RunId = turn.RunId,
        Status = turn.RunStatus,
        Summary = turn.Result is { Length: > 0 } r ? r : CollapsedSummary(turn.RunStatus),
        Map = null,
        Blocks = Array.Empty<RoomBlock>(),
        Actions = _actions.ResolveTurnActions(turn.RunId, turn.RunStatus),
        At = turn.CreatedDate,
    };

    private static string CollapsedSummary(Messages.Enums.WorkflowRunStatus status) => status switch
    {
        Messages.Enums.WorkflowRunStatus.Success => "Done.",
        Messages.Enums.WorkflowRunStatus.Failure => "Ended with an error.",
        Messages.Enums.WorkflowRunStatus.Cancelled => "Cancelled.",
        Messages.Enums.WorkflowRunStatus.Suspended => "Waiting for input.",
        _ => "Working…",
    };

    /// <summary>The run's append-only change watermark — MAX(Sequence) over its records, 0 before any record. The streaming cursor + the focused turn's block Seq.</summary>
    private async Task<long> WatermarkAsync(Guid runId, CancellationToken cancellationToken) =>
        await _db.WorkflowRunRecord.AsNoTracking().Where(r => r.RunId == runId).MaxAsync(r => (long?)r.Sequence, cancellationToken).ConfigureAwait(false) ?? 0;

    /// <summary>
    /// The pending decisions parked on this run — node-grain (matched by the run id) or agent-grain (matched by one of
    /// the run's own agent runs; an agent-grain envelope carries no run id, so we resolve the run's agents directly
    /// rather than via the phase tree, which catches a decision even when its agent isn't phase-surfaced). Only reached
    /// when the turn skeleton already reported a pending decision, so the team-wide pending read fires for that case only.
    /// </summary>
    private async Task<IReadOnlyList<DecisionBlock>> DecisionBlocksAsync(Guid runId, Guid teamId, long seq, CancellationToken cancellationToken)
    {
        var agentIds = (await _db.AgentRun.AsNoTracking()
            .Where(a => a.WorkflowRunId == runId && a.TeamId == teamId)
            .Select(a => a.Id)
            .ToListAsync(cancellationToken).ConfigureAwait(false)).ToHashSet();

        var pending = await _decisions.ListPendingAsync(teamId, cancellationToken).ConfigureAwait(false);

        return pending
            .Where(d => d.WorkflowRunId == runId || (d.AgentRunId is { } a && agentIds.Contains(a)))
            .Select(d => ToDecisionBlock(d, seq))
            .ToList();
    }

    private static DecisionBlock ToDecisionBlock(PendingDecision d, long seq) => new()
    {
        Id = $"decision-{d.Id}",
        Seq = seq,
        DecisionId = d.Id,
        Question = d.Question,
        Shape = d.DecisionType,
        Options = d.Options.Count > 0 ? d.Options.Select(o => new RoomDecisionOption { Id = o.Id, Label = o.Label, SideEffecting = o.IsSideEffecting }).ToList() : null,
        Risk = d.RiskLevel,
        Deadline = d.DeadlineAt,
    };
}
