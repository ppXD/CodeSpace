using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Services.Decisions;
using CodeSpace.Core.Services.Supervisor;
using CodeSpace.Core.Services.Tasks.Phases;
using CodeSpace.Messages.Agents;
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
    private readonly ISupervisorDecisionLog _decisionLog;
    private readonly CodeSpaceDbContext _db;

    public RoomProjector(ISessionReadService sessions, IRunPhaseProjector phases, IDecisionQueueService decisions, IRunActionCapabilityResolver actions, ISupervisorDecisionLog decisionLog, CodeSpaceDbContext db)
    {
        _sessions = sessions;
        _phases = phases;
        _decisions = decisions;
        _actions = actions;
        _decisionLog = decisionLog;
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

        var facts = await GatherFactsAsync(runId, teamId, phases, turn.Error, cancellationToken).ConfigureAwait(false);

        var narrative = RoomNarrative.Build($"turn-{turn.TurnIndex}", watermark, phases, turn.RunStatus, turn.Error, decisions, facts);

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
            DurationMs = DurationMs(turn),
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
        DurationMs = DurationMs(turn),
    };

    /// <summary>The turn's wall-clock — final once the run completed, else live elapsed since it started; null before it starts.</summary>
    private static long? DurationMs(SessionTurn turn)
    {
        if (turn.StartedAt is not { } start) return null;

        var ms = (long)((turn.CompletedAt ?? DateTimeOffset.UtcNow) - start).TotalMilliseconds;

        return ms >= 0 ? ms : null;
    }

    private static string CollapsedSummary(Messages.Enums.WorkflowRunStatus status) => status switch
    {
        Messages.Enums.WorkflowRunStatus.Success => "Done.",
        Messages.Enums.WorkflowRunStatus.Failure => "Ended with an error.",
        Messages.Enums.WorkflowRunStatus.Cancelled => "Cancelled.",
        Messages.Enums.WorkflowRunStatus.Suspended => "Waiting for input.",
        _ => "Working…",
    };

    /// <summary>
    /// Gather the focused turn's facts from the substrate — one decision-tape read (subtasks · changed files ·
    /// acceptance), one batched tool-count, one reasoning COUNT (never the text), and the PR node-join. All scoped to
    /// this run / its agents, so the cost scales with the turn, not the database. The pure narrative engine consumes these.
    /// </summary>
    private async Task<RoomTurnFacts> GatherFactsAsync(Guid runId, Guid teamId, IReadOnlyList<RunPhase> phases, string? error, CancellationToken cancellationToken)
    {
        var decisions = await _decisionLog.GetForRunAsync(runId, teamId, cancellationToken).ConfigureAwait(false);

        var plan = decisions.LastOrDefault(d => d.DecisionKind == SupervisorDecisionKinds.Plan);
        var subtasks = SupervisorOutcome.ReadPlanSubtasks(plan?.PayloadJson).Select(s => s.Title).ToList();

        // The turn's agent results (latest fold per agent) — the one read drives the changed-file list, the per-agent
        // card summaries, and the lead fallback (no stop summary → compose from these).
        var agentResults = decisions
            .Where(d => SupervisorDecisionKinds.StagesAgents(d.DecisionKind))
            .SelectMany(d => SupervisorOutcome.ReadAgentResults(d.OutcomeJson))
            .GroupBy(r => r.AgentRunId).Select(g => g.Last())
            .ToList();

        var changedFiles = agentResults
            .SelectMany(r => r.ChangedFiles)
            .Distinct(StringComparer.Ordinal).OrderBy(p => p, StringComparer.Ordinal).Take(MaxChangedFiles).ToList();

        var agentSummaries = agentResults
            .Where(r => !string.IsNullOrWhiteSpace(r.Summary))
            .ToDictionary(r => r.AgentRunId, r => r.Summary!.Trim());

        var stop = decisions.LastOrDefault(d => d.DecisionKind == SupervisorDecisionKinds.Stop);
        var acceptance = SupervisorOutcome.ReadAcceptanceGradePassed(stop?.OutcomeJson);

        var agentIds = phases.SelectMany(p => p.Agents).Select(a => a.AgentRunId).Distinct().ToList();

        // Tool total from the already-projected phases (the phase source folded the per-agent count) — no second ledger
        // query, and the room's "N tool calls" can't diverge from the board. Dedup by agent (an agent appears in both a
        // decision phase + an authored phase). The reasoning COUNT is a distinct metric, so it stays one bounded query.
        int? toolCalls = agentIds.Count == 0 ? null : phases.SelectMany(p => p.Agents).GroupBy(a => a.AgentRunId).Sum(g => g.First().ToolCount ?? 0);

        var reasoningCount = agentIds.Count == 0 ? 0 : await _db.AgentRunEvent.AsNoTracking()
            .Where(e => agentIds.Contains(e.AgentRunId) && e.Kind == AgentEventKind.Reasoning)
            .CountAsync(cancellationToken).ConfigureAwait(false);

        // The reasoning step texts, bounded to the focused turn (cap the count, so a huge run stays cheap) — surfaced
        // when the Reasoning row is expanded. Public reasoning narration (the harness emits summaries, not raw CoT).
        var reasoningSteps = reasoningCount == 0 ? new List<string>() : await _db.AgentRunEvent.AsNoTracking()
            .Where(e => agentIds.Contains(e.AgentRunId) && e.Kind == AgentEventKind.Reasoning && e.Text != null && e.Text != "")
            .OrderBy(e => e.OccurredAt)
            .Select(e => e.Text!)
            .Take(MaxReasoningSteps)
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        // The per-kind tool histogram (read · edit · test) — one GROUP BY scoped to the turn's agents, side-effecting
        // rows only (DecisionEnvelopeJson == null), matching the ToolCount semantics. No N+1.
        var toolHistogram = agentIds.Count == 0 ? new List<ToolKindCount>() : (await _db.ToolCallLedger.AsNoTracking()
            .Where(t => agentIds.Contains(t.AgentRunId) && t.TeamId == teamId && t.DecisionEnvelopeJson == null)
            .GroupBy(t => t.ToolKind)
            .Select(g => new { Kind = g.Key, Count = g.Count() })
            .ToListAsync(cancellationToken).ConfigureAwait(false))
            .Select(x => new ToolKindCount(x.Kind, x.Count))
            .OrderByDescending(x => x.Count).ThenBy(x => x.Kind, StringComparer.Ordinal).ToList();

        return new RoomTurnFacts
        {
            Subtasks = subtasks,
            ChangedFiles = changedFiles,
            ToolCalls = toolCalls,
            ToolHistogram = toolHistogram,
            ReasoningCount = reasoningCount,
            ReasoningSteps = reasoningSteps,
            AgentSummaries = agentSummaries,
            AcceptancePassed = acceptance,
            Delivery = await DeliveryAsync(runId, cancellationToken).ConfigureAwait(false),
            RawError = error,
        };
    }

    private const int MaxChangedFiles = 200;
    private const int MaxReasoningSteps = 40;

    /// <summary>The PR the turn opened, joined from the run's open-PR node output (number/url) + its inputs (title / branches). Null when the turn opened none.</summary>
    private async Task<RoomDelivery?> DeliveryAsync(Guid runId, CancellationToken cancellationToken)
    {
        var nodes = await _db.WorkflowRunNode.AsNoTracking()
            .Where(n => n.RunId == runId)
            .Select(n => new { n.OutputsJson, n.InputsJson })
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        return nodes.Select(n => RoomDeliveryParser.Parse(n.OutputsJson, n.InputsJson)).FirstOrDefault(d => d != null);
    }

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
