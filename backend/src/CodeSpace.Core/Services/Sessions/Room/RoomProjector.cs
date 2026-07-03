using System.Text.Json;
using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Decisions;
using CodeSpace.Core.Services.Plans;
using CodeSpace.Core.Services.Supervisor;
using CodeSpace.Core.Services.Tasks.Phases;
using CodeSpace.Core.Services.Tasks.Timeline.Sources;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Constants;
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

        // The requested run IS the anchor — so opening a prior attempt's run focuses THAT attempt's flow, not the latest.
        return detail == null ? null : await BuildAsync(detail, detail.AnchorTurnIndex, runId, teamId, cancellationToken).ConfigureAwait(false);
    }

    public async Task<RoomView?> ProjectAsync(Guid sessionId, Guid? focusRunId, Guid teamId, CancellationToken cancellationToken)
    {
        var detail = await _sessions.GetDetailAsync(sessionId, teamId, cancellationToken).ConfigureAwait(false);

        if (detail == null) return null;

        var focus = focusRunId is { } fr ? TurnIndexOf(detail, fr) : null;

        return await BuildAsync(detail, focus, focusRunId, teamId, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The turn a run belongs to — its identity, the latest attempt, or any nested attempt. Null when the run isn't a turn here.</summary>
    private static int? TurnIndexOf(SessionDetail detail, Guid runId) =>
        detail.Turns.FirstOrDefault(t => t.TurnRunId == runId || t.RunId == runId || (t.Attempts?.Any(a => a.RunId == runId) ?? false))?.TurnIndex;

    private async Task<RoomView> BuildAsync(SessionDetail detail, int? focusTurnIndex, Guid? anchorRunId, Guid teamId, CancellationToken cancellationToken)
    {
        var focused = (focusTurnIndex is { } fi ? detail.Turns.FirstOrDefault(t => t.TurnIndex == fi) : null) ?? detail.Turns.LastOrDefault();

        var blocks = new List<RoomBlock>();
        long cursor = 0;

        foreach (var turn in detail.Turns)
        {
            if (turn.UserMessage is { Length: > 0 } message)
                blocks.Add(new UserMessageBlock { Id = $"turn-{turn.TurnIndex}:user", Seq = 0, Text = message, At = turn.CreatedDate });

            var assistant = focused != null && turn.TurnIndex == focused.TurnIndex
                ? await BuildFocusedTurnAsync(turn, anchorRunId, teamId, cancellationToken).ConfigureAwait(false)
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

    private async Task<AssistantTurnBlock> BuildFocusedTurnAsync(SessionTurn turn, Guid? anchorRunId, Guid teamId, CancellationToken cancellationToken)
    {
        // Focus the REQUESTED attempt (the anchor run), so the header switcher can show ANY attempt's whole flow — not
        // always the latest. A prior attempt carries its own status / error / timing (the turn skeleton has the latest's).
        var focus = await FocusAsync(turn, anchorRunId, teamId, cancellationToken).ConfigureAwait(false);
        var runId = focus.RunId;

        var phases = await _phases.ProjectAsync(runId, teamId, cancellationToken).ConfigureAwait(false) ?? Array.Empty<RunPhase>();
        var watermark = await WatermarkAsync(runId, cancellationToken).ConfigureAwait(false);

        // Skip the pending-decision read entirely in the common case — the turn skeleton already knows whether this
        // run is parked on one (computed over both park backends), so a non-waiting turn pays zero query + zero parse.
        var decisions = turn.HasPendingDecision && focus.IsLatest
            ? await DecisionBlocksAsync(runId, teamId, watermark, cancellationToken).ConfigureAwait(false)
            : Array.Empty<DecisionBlock>();

        var facts = await GatherFactsAsync(runId, teamId, phases, focus.Status, focus.Error, cancellationToken).ConfigureAwait(false);

        var narrative = RoomNarrative.Build($"turn-{turn.TurnIndex}", watermark, phases, focus.Status, focus.Error, decisions, facts);

        return new AssistantTurnBlock
        {
            Id = $"turn-{turn.TurnIndex}",
            Seq = watermark,
            TurnIndex = turn.TurnIndex,
            TurnRunId = turn.TurnRunId,
            RunId = runId,
            Status = focus.Status,
            Summary = narrative.Summary,
            Map = narrative.Map,
            Blocks = narrative.Blocks,
            Actions = _actions.ResolveTurnActions(runId, focus.Status),
            At = focus.CreatedDate,
            DurationMs = DurationOf(focus.CreatedDate, focus.StartedAt, focus.CompletedAt),
            Attempts = AttemptsOf(turn, runId),
        };
    }

    private sealed record FocusRun(Guid RunId, Messages.Enums.WorkflowRunStatus Status, string? Error, DateTimeOffset CreatedDate, DateTimeOffset? StartedAt, DateTimeOffset? CompletedAt, bool IsLatest);

    /// <summary>
    /// Resolve which attempt to focus. Reads the ANCHOR run's OWN status / error / timing whenever it's one of this
    /// turn's attempts — INCLUDING the latest. The turn skeleton's <c>CreatedDate</c> is the lineage ROOT's (attempt 1),
    /// so a multi-attempt turn's latest would otherwise be dated to attempt 1 and measure the WHOLE-lineage span (days
    /// across reruns) instead of that attempt's own wall-clock. A single-attempt turn (no ladder), or a run that isn't
    /// one of this turn's attempts, reuses the skeleton — no extra read (the skeleton IS the single run's own row).
    /// </summary>
    private async Task<FocusRun> FocusAsync(SessionTurn turn, Guid? anchorRunId, Guid teamId, CancellationToken cancellationToken)
    {
        var latest = new FocusRun(turn.RunId, turn.RunStatus, turn.Error, turn.CreatedDate, turn.StartedAt, turn.CompletedAt, IsLatest: true);

        if (anchorRunId is not { } anchor || (turn.Attempts?.All(a => a.RunId != anchor) ?? true))
            return latest;

        var row = await _db.WorkflowRun.AsNoTracking()
            .Where(r => r.Id == anchor && r.TeamId == teamId)
            .Select(r => new { r.Status, r.Error, r.CreatedDate, r.StartedAt, r.CompletedAt })
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);

        return row is null ? latest : new FocusRun(anchor, row.Status, row.Error, row.CreatedDate, row.StartedAt, row.CompletedAt, IsLatest: anchor == turn.RunId);
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
        Attempts = AttemptsOf(turn, turn.RunId),
    };

    /// <summary>The turn's attempt timeline (oldest → newest) — projected only when it was rerun (&gt; 1 attempt). <paramref name="focusRunId"/> marks the shown one (the attempt the room is currently focused on), so switching to a prior attempt re-marks it "shown".</summary>
    private static IReadOnlyList<RoomTurnAttempt> AttemptsOf(SessionTurn turn, Guid focusRunId)
    {
        var attempts = turn.Attempts ?? Array.Empty<SessionTurnAttempt>();

        if (attempts.Count < 2) return Array.Empty<RoomTurnAttempt>();

        return attempts
            .OrderBy(a => a.AttemptNumber)
            .Select(a => new RoomTurnAttempt { RunId = a.RunId, AttemptNumber = a.AttemptNumber, Status = a.Status, At = a.CreatedDate, IsCurrent = a.RunId == focusRunId })
            .ToList();
    }

    /// <summary>
    /// The turn's wall-clock. A COMPLETED turn measures <c>CompletedAt − CreatedDate</c> — anchored on the immutable
    /// enqueue time, NOT <c>StartedAt</c>, because a resumed / re-dispatched run (e.g. recovered after a restart) resets
    /// StartedAt to its final leg, which would under-report the whole-turn elapsed (28m read as 36s). A LIVE turn shows
    /// elapsed since it actually started (null before then, so a queued turn shows no growing time).
    /// </summary>
    private static long? DurationMs(SessionTurn turn) => DurationOf(turn.CreatedDate, turn.StartedAt, turn.CompletedAt);

    private static long? DurationOf(DateTimeOffset createdDate, DateTimeOffset? startedAt, DateTimeOffset? completedAt)
    {
        if (completedAt is { } end)
        {
            var span = (long)(end - createdDate).TotalMilliseconds;
            return span >= 0 ? span : null;
        }

        if (startedAt is not { } start) return null;

        var ms = (long)(DateTimeOffset.UtcNow - start).TotalMilliseconds;
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

        var agentIds = phases.SelectMany(p => p.Agents).Select(a => a.AgentRunId).Distinct().ToList();

        // The turn's agent results (latest fold per agent) — the one read drives the changed-file list, the per-agent
        // card summaries, and the lead fallback (no stop summary → compose from these).
        var agentResults = decisions
            .Where(d => SupervisorDecisionKinds.StagesAgents(d.DecisionKind))
            .SelectMany(d => SupervisorOutcome.ReadAgentResults(d.OutcomeJson))
            .GroupBy(r => r.AgentRunId).Select(g => g.Last())
            .ToList();

        // A single-agent / non-supervisor run has an EMPTY decision tape, so the fold above is empty. Source its result
        // straight from the run's own AgentRun rows (the persisted AgentRunResult — summary + git-ground-truth changed
        // files) so a plain agent turn still shows a RESULT + its output, not just the execution dots.
        if (decisions.Count == 0 && agentIds.Count > 0)
            agentResults = await ReadAgentRunResultsAsync(agentIds, cancellationToken).ConfigureAwait(false);

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

        // The delivered answer text: the supervisor's stop summary, else — for a single-agent run with no supervisor —
        // that one agent's own final summary (its result IS the answer). A multi-agent run without a supervisor has no
        // synthesized answer, so it falls to the file attachments alone.
        var finalAnswerText = SupervisorOutcome.ReadStopSummary(stop?.OutcomeJson)
            ?? (decisions.Count == 0 && agentResults.Count == 1 ? agentResults[0].Summary : null);

        // The retry beats — one per retry decision, in tape order — each carrying its FRESH agent so the narrative can
        // render "Supervisor retried a subtask" + that agent's own card, chronologically. Reuses the shared timeline
        // copy authority for the line; a no-op retry (nothing staged) carries no agent.
        var retrySteps = decisions
            .Where(d => d.DecisionKind == SupervisorDecisionKinds.Retry)
            .OrderBy(d => d.Sequence)
            .Select(d =>
            {
                var agentId = SupervisorOutcome.ReadStagedAgentRunIds(d.OutcomeJson).FirstOrDefault();
                var (why, evidence) = SupervisorOutcome.ReadRetryRationale(d.PayloadJson);

                return new RoomRetryStep(d.Sequence, SupervisorDecisionTimelineMap.RetryTitle(d), agentId == Guid.Empty ? null : agentId, FormatRationale(why, evidence));
            })
            .ToList();

        // The re-spawn waves — an additional Spawn decision that re-dispatched an ALREADY-spawned subtask (a second
        // wave, e.g. after a no-op retry the supervisor re-ran the work). The authored phase group anchors only each
        // subtask's FIRST attempt, so a later wave (and its failed agent) is otherwise dropped — Activity shows it, the
        // room didn't. Surface each such wave so the room renders the whole trajectory. Empty for a single-wave run.
        var respawnSteps = RespawnWaves(decisions);

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

        // The DEEPEST failure error — the run row's Error is a generic "Node 'sup' failed."; the real cause (an OpenAI
        // timeout, a rejected credential) lives on the node.failed / interaction.failed ledger record the engine wrote.
        // Only read it on a failed / cancelled turn (the only case the diagnostic renders), so a live / successful turn
        // pays zero extra query — the diagnostic then shows the SPECIFIC error Activity does, not the placeholder.
        var deepError = status is Messages.Enums.WorkflowRunStatus.Failure or Messages.Enums.WorkflowRunStatus.Cancelled
            ? await DeepFailureErrorAsync(runId, cancellationToken).ConfigureAwait(false)
            : null;

        return new RoomTurnFacts
        {
            Rounds = rounds,
            Checklist = checklist,
            FinalAnswer = BuildFinalAnswer(finalAnswerText, changedFiles, delivery),
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
            RawError = deepError ?? error,
            RetrySteps = retrySteps,
            RespawnSteps = respawnSteps,
        };
    }

    /// <summary>
    /// The RE-SPAWN waves — walking the run's Spawn decisions (after the latest plan, since subtask ids are plan-local)
    /// in tape order and flagging each subtask's SECOND-and-later spawn. The first spawn of a subtask anchors the
    /// authored phase group; a later spawn is a fresh wave the group can't hold, so each additional Spawn decision that
    /// re-dispatched an already-seen subtask becomes one <see cref="RoomRespawnStep"/> carrying just the re-spawned agents
    /// (a subtask's first-ever spawn in a mixed wave stays in its phase group, never double-rendered). Empty when every
    /// subtask ran once. Mirrors <see cref="SupervisorPhaseSource"/>'s attempt walk so the two can't disagree on "wave 1".
    /// </summary>
    private static IReadOnlyList<RoomRespawnStep> RespawnWaves(IReadOnlyList<SupervisorDecisionRecord> decisions)
    {
        var plan = decisions.LastOrDefault(d => d.DecisionKind == SupervisorDecisionKinds.Plan);

        if (plan == null) return Array.Empty<RoomRespawnStep>();

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var waves = new List<RoomRespawnStep>();

        foreach (var d in decisions.Where(d => d.Sequence > plan.Sequence && d.DecisionKind == SupervisorDecisionKinds.Spawn).OrderBy(d => d.Sequence))
        {
            var subtaskIds = SupervisorOutcome.ReadSpawnSubtaskIds(d.PayloadJson);
            var agentIds = SupervisorOutcome.ReadStagedAgentRunIds(d.OutcomeJson);

            var respawned = new List<Guid>();

            for (var i = 0; i < Math.Min(subtaskIds.Count, agentIds.Count); i++)
                if (!seen.Add(subtaskIds[i]) && agentIds[i] != Guid.Empty)   // Add == false → this subtask was already spawned → a re-spawn
                    respawned.Add(agentIds[i]);

            if (respawned.Count > 0) waves.Add(new RoomRespawnStep(d.Sequence, respawned));
        }

        return waves;
    }

    /// <summary>The deepest specific failure error — the newest node.failed / interaction.failed ledger record's <c>error</c>, preferring a TOP-LEVEL failure (empty iteration key — the node that actually failed the run) over a fanned-out branch's per-iteration error. Null when no such record carries an error (the caller then falls back to the generic run error).</summary>
    private async Task<string?> DeepFailureErrorAsync(Guid runId, CancellationToken cancellationToken)
    {
        var rows = await _db.WorkflowRunRecord.AsNoTracking()
            .Where(r => r.RunId == runId && (r.RecordType == WorkflowRunRecordTypes.NodeFailed || r.RecordType == WorkflowRunRecordTypes.InteractionFailed))
            .OrderByDescending(r => r.Sequence)
            .Select(r => new { r.IterationKey, r.PayloadJson })
            .Take(MaxFailureScan)
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        var best = rows.FirstOrDefault(r => string.IsNullOrEmpty(r.IterationKey)) ?? rows.FirstOrDefault();

        return best is null ? null : ReadRecordError(best.PayloadJson);
    }

    /// <summary>Parse the <c>error</c> string out of a ledger record's payload — the deep failure message. Null for a missing / non-string / malformed payload.</summary>
    private static string? ReadRecordError(string? payloadJson)
    {
        if (string.IsNullOrEmpty(payloadJson)) return null;

        try
        {
            using var doc = JsonDocument.Parse(payloadJson);
            return doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("error", out var e) && e.ValueKind == JsonValueKind.String && e.GetString() is { Length: > 0 } s ? s : null;
        }
        catch (JsonException) { return null; }
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

    /// <summary>The retry's structured rationale as one readable line — the reason, then the evidence it acted on. Null when the model authored neither.</summary>
    private static string? FormatRationale(string? why, string? evidence)
    {
        var reason = string.IsNullOrWhiteSpace(why) ? null : why.Trim();
        var basis = string.IsNullOrWhiteSpace(evidence) ? null : $"Evidence: {evidence.Trim()}";

        return string.Join(" · ", new[] { reason, basis }.Where(p => p != null)) is { Length: > 0 } line ? line : null;
    }

    /// <summary>
    /// The run's OWN agent results, read straight from the durable AgentRun rows — the non-supervisor path (empty
    /// decision tape). Projects each row's persisted <c>AgentRunResult</c> (summary + git-ground-truth changed files)
    /// into the same <see cref="SupervisorAgentResult"/> shape the tape fold yields, so the downstream projection
    /// (changed files · card summaries · final answer) is identical for a plain agent turn.
    /// </summary>
    private async Task<List<SupervisorAgentResult>> ReadAgentRunResultsAsync(IReadOnlyList<Guid> agentIds, CancellationToken cancellationToken)
    {
        var rows = await _db.AgentRun.AsNoTracking()
            .Where(r => agentIds.Contains(r.Id))
            .Select(r => new { r.Id, r.Status, r.ResultJson })
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        return rows.Select(r =>
        {
            var result = string.IsNullOrWhiteSpace(r.ResultJson) ? null : JsonSerializer.Deserialize<AgentRunResult>(r.ResultJson!, AgentJson.Options);

            return new SupervisorAgentResult
            {
                AgentRunId = r.Id,
                Status = r.Status.ToString(),
                Summary = result?.Summary,
                ChangedFiles = result?.ChangedFiles ?? Array.Empty<string>(),
            };
        }).ToList();
    }

    private const int MaxChangedFiles = 200;
    private const int MaxAgentFiles = 40;
    private const int MaxReasoningSteps = 40;
    private const int MaxLatestLineScan = 200;
    private const int MaxAnswerFiles = 40;
    private const int MaxToolScan = 2000;
    private const int MaxFailureScan = 50;

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
