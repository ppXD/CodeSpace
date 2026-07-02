using System.Text.Json;
using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Services.Decisions;
using CodeSpace.Core.Services.Plans;
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
    private readonly IWorkPlanChecklistService _checklists;
    private readonly CodeSpaceDbContext _db;

    public RoomProjector(ISessionReadService sessions, IRunPhaseProjector phases, IDecisionQueueService decisions, IRunActionCapabilityResolver actions, ISupervisorDecisionLog decisionLog, IWorkPlanChecklistService checklists, CodeSpaceDbContext db)
    {
        _sessions = sessions;
        _phases = phases;
        _decisions = decisions;
        _actions = actions;
        _decisionLog = decisionLog;
        _checklists = checklists;
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

        var facts = await GatherFactsAsync(runId, teamId, phases, turn.RunStatus, turn.Error, cancellationToken).ConfigureAwait(false);

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
    private async Task<RoomTurnFacts> GatherFactsAsync(Guid runId, Guid teamId, IReadOnlyList<RunPhase> phases, Messages.Enums.WorkflowRunStatus status, string? error, CancellationToken cancellationToken)
    {
        var decisions = await _decisionLog.GetForRunAsync(runId, teamId, cancellationToken).ConfigureAwait(false);

        // The supervisor rounds, segmented on each Plan (a re-plan opens a new round) — the render source (never lumped).
        var rounds = RoomRounds.Segment(decisions);

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

        // Per-agent file attribution (B): each agent's OWN changed files, so a card shows WHICH agent produced a file
        // rather than the provenance-blind turn-level union. Bounded per agent; an agent that changed nothing is omitted.
        var agentFiles = agentResults
            .Where(r => r.ChangedFiles.Count > 0)
            .ToDictionary(r => r.AgentRunId, r => (IReadOnlyList<string>)r.ChangedFiles.Take(MaxAgentFiles).ToList());

        var stop = decisions.LastOrDefault(d => d.DecisionKind == SupervisorDecisionKinds.Stop);
        var acceptance = SupervisorOutcome.ReadAcceptanceGradePassed(stop?.OutcomeJson);

        var agentIds = phases.SelectMany(p => p.Agents).Select(a => a.AgentRunId).Distinct().ToList();

        // The turn's tool-call TOTAL — summed from the already-projected per-agent counts (no extra query; the same
        // figure the agent cards show). Dedup by agent (an agent can appear in both a decision + an authored phase).
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

        // The per-TOOL histogram (Read · WebSearch · Write · …) — grouped by the tool NAME parsed from each ToolCall
        // event's payload (data.name), NOT the event text (which for some tools is a path / description, so grouping on
        // it produced noisy pseudo-"tools"). One bounded fetch of the payloads, grouped in-memory.
        var toolPayloads = agentIds.Count == 0 ? new List<string?>() : await _db.AgentRunEvent.AsNoTracking()
            .Where(e => agentIds.Contains(e.AgentRunId) && e.Kind == AgentEventKind.ToolCall)
            .OrderBy(e => e.Sequence)
            .Select(e => e.DataJson)
            .Take(MaxToolScan)
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        var toolHistogram = toolPayloads
            .GroupBy(ToolName)
            .Select(g => new ToolKindCount(g.Key, g.Count()))
            .OrderByDescending(x => x.Count).ThenBy(x => x.Kind, StringComparer.Ordinal).ToList();

        // The live "working…" indicator source — the latest PUBLIC activity line per agent (never reasoning). Only for an
        // ACTIVE turn (a settled turn never renders the indicator), so a finished turn pays zero query.
        var active = status is Messages.Enums.WorkflowRunStatus.Pending or Messages.Enums.WorkflowRunStatus.Enqueued or Messages.Enums.WorkflowRunStatus.Running or Messages.Enums.WorkflowRunStatus.Suspended;

        var latestLines = !active || agentIds.Count == 0 ? new Dictionary<Guid, string>() : (await _db.AgentRunEvent.AsNoTracking()
            .Where(e => agentIds.Contains(e.AgentRunId) && e.Kind != AgentEventKind.Reasoning && e.Text != null && e.Text != "")
            .OrderByDescending(e => e.OccurredAt)
            .Select(e => new { e.AgentRunId, e.Text })
            .Take(MaxLatestLineScan)
            .ToListAsync(cancellationToken).ConfigureAwait(false))
            .GroupBy(e => e.AgentRunId)
            .ToDictionary(g => g.Key, g => g.First().Text!.Trim());

        var delivery = await DeliveryAsync(runId, cancellationToken).ConfigureAwait(false);

        // The run's durable plan checklist (contract + tape-derived states) — null for pre-plan runs, which then
        // project exactly as before (the per-round plan stat rows carry the story).
        var checklist = await _checklists.GetCurrentAsync(runId, teamId, cancellationToken).ConfigureAwait(false);

        return new RoomTurnFacts
        {
            Rounds = rounds,
            Checklist = checklist,
            FinalAnswer = BuildFinalAnswer(SupervisorOutcome.ReadStopSummary(stop?.OutcomeJson), changedFiles, delivery),
            LatestLines = latestLines,
            AgentFiles = agentFiles,
            Subtasks = subtasks,
            ChangedFiles = changedFiles,
            ToolCalls = toolCalls,
            ToolHistogram = toolHistogram,
            ReasoningCount = reasoningCount,
            ReasoningSteps = reasoningSteps,
            AgentSummaries = agentSummaries,
            AcceptancePassed = acceptance,
            Delivery = delivery,
            RawError = error,
        };
    }

    /// <summary>The rich final answer — the stop summary text + typed attachments (the changed files + the PR). Images are a true gap (no run output exposes them). Null when there's nothing to deliver.</summary>
    private static RoomFinalAnswer? BuildFinalAnswer(string? text, IReadOnlyList<string> files, RoomDelivery? pr)
    {
        var attachments = new List<RoomAttachment>();

        foreach (var f in files.Take(MaxAnswerFiles))
            attachments.Add(new RoomAttachment(AnswerAttachmentKind.FileLink, f, Url: null, PreviewUrl: null, DownloadUrl: null));

        if (pr is { } d)
            attachments.Add(new RoomAttachment(AnswerAttachmentKind.Pr, d.Reference is { Length: > 0 } r ? $"{d.Title} {r}" : d.Title, Url: d.Url, PreviewUrl: null, DownloadUrl: null));

        var body = string.IsNullOrWhiteSpace(text) ? null : text.Trim();

        return body == null && attachments.Count == 0 ? null : new RoomFinalAnswer { Text = body, Attachments = attachments };
    }

    private const int MaxChangedFiles = 200;
    private const int MaxAgentFiles = 40;
    private const int MaxReasoningSteps = 40;
    private const int MaxLatestLineScan = 200;
    private const int MaxAnswerFiles = 40;
    private const int MaxToolScan = 2000;

    /// <summary>The tool NAME from a ToolCall event's payload (<c>data.name</c>, e.g. "Read" / "WebSearch") — the clean grouping key for the histogram. Falls back to "tool" for a missing / malformed payload.</summary>
    private static string ToolName(string? dataJson)
    {
        if (string.IsNullOrEmpty(dataJson)) return "tool";

        try
        {
            using var doc = JsonDocument.Parse(dataJson);
            return doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String && n.GetString() is { Length: > 0 } name
                ? name : "tool";
        }
        catch (JsonException) { return "tool"; }
    }

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
