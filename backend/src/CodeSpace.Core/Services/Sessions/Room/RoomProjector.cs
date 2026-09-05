using System.Text.Json;
using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Agents.Publish;
using CodeSpace.Core.Services.Decisions;
using CodeSpace.Core.Services.Plans;
using CodeSpace.Core.Services.Supervisor;
using CodeSpace.Core.Services.Tasks.Phases;
using CodeSpace.Core.Services.Tasks.Timeline.Sources;
using CodeSpace.Core.Services.Workflows;
using CodeSpace.Core.Services.Workflows.Artifacts;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Dtos.Decisions;
using CodeSpace.Messages.Dtos.Sessions;
using CodeSpace.Messages.Dtos.Sessions.Room;
using CodeSpace.Messages.Tasks.Phases;
using Microsoft.EntityFrameworkCore;

namespace CodeSpace.Core.Services.Sessions.Room;

/// <summary>
/// Default <see cref="IRoomProjector"/>. Reuses the narrow turn skeleton from <see cref="ISessionSkeletonReader"/> (one query,
/// goals + latest-attempt run id + status per turn) and enriches EVERY turn with the heavy projections — the phase
/// tree (<see cref="IRunPhaseProjector"/>), the pending decisions, the capability-aware actions, and the change
/// watermark — so each turn's full execution UI is available on expand, not just the focused one. The focused turn
/// honours the requested attempt (anchor run); every other turn focuses its own latest run. All copy / order lives in
/// the pure <see cref="RoomNarrative"/>. READ-ONLY. (Past turns are terminal, so their projection is stable and a
/// candidate for caching to avoid re-reading immutable turns on every live poll.)
/// </summary>
internal sealed class RoomProjector : IRoomProjector, IScopedDependency
{
    private readonly ISessionSkeletonReader _sessions;
    private readonly IRunPhaseProjector _phases;
    private readonly IDecisionQueueService _decisions;
    private readonly IRunActionCapabilityResolver _actions;
    private readonly ISupervisorDecisionObservationBundle _decisionObservations;
    private readonly IWorkPlanChecklistService _checklists;
    private readonly IPublishManifestStore _manifests;
    private readonly IArtifactManifestStore _producedFiles;
    private readonly ISupervisorPublishedBranchResolver _publishedBranches;
    private readonly IArtifactRangeReader _artifacts;
    private readonly CodeSpaceDbContext _db;
    private readonly ISessionTurnCache _cache;

    public RoomProjector(ISessionSkeletonReader sessions, IRunPhaseProjector phases, IDecisionQueueService decisions, IRunActionCapabilityResolver actions, ISupervisorDecisionObservationBundle decisionObservations, IWorkPlanChecklistService checklists, IPublishManifestStore manifests, IArtifactManifestStore producedFiles, ISupervisorPublishedBranchResolver publishedBranches, IArtifactRangeReader artifacts, CodeSpaceDbContext db, ISessionTurnCache cache)
    {
        _sessions = sessions;
        _phases = phases;
        _decisions = decisions;
        _actions = actions;
        _decisionObservations = decisionObservations;
        _checklists = checklists;
        _manifests = manifests;
        _producedFiles = producedFiles;
        _publishedBranches = publishedBranches;
        _artifacts = artifacts;
        _db = db;
        _cache = cache;
    }

    public async Task<RoomView?> ProjectByRunAsync(Guid runId, Guid teamId, CancellationToken cancellationToken)
    {
        var detail = await _sessions.GetByRunAsync(runId, teamId, cancellationToken).ConfigureAwait(false);

        // The requested run IS the anchor — so opening a prior attempt's run focuses THAT attempt's flow, not the latest.
        return detail == null ? null : await BuildAsync(detail, detail.AnchorTurnIndex, runId, teamId, cancellationToken).ConfigureAwait(false);
    }

    public async Task<RoomView?> ProjectAsync(Guid sessionId, Guid? focusRunId, Guid teamId, CancellationToken cancellationToken)
    {
        var detail = await _sessions.GetBySessionAsync(sessionId, teamId, cancellationToken).ConfigureAwait(false);

        if (detail == null) return null;

        var focus = focusRunId is { } fr ? TurnIndexOf(detail, fr) : null;

        return await BuildAsync(detail, focus, focusRunId, teamId, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The turn a run belongs to — its identity, the latest attempt, or any nested attempt. Null when the run isn't a turn here.</summary>
    private static int? TurnIndexOf(SessionSkeleton detail, Guid runId) =>
        detail.Turns.FirstOrDefault(t => t.TurnRunId == runId || t.RunId == runId || (t.Attempts?.Any(a => a.RunId == runId) ?? false))?.TurnIndex;

    private async Task<RoomView> BuildAsync(SessionSkeleton detail, int? focusTurnIndex, Guid? anchorRunId, Guid teamId, CancellationToken cancellationToken)
    {
        var focused = (focusTurnIndex is { } fi ? detail.Turns.FirstOrDefault(t => t.TurnIndex == fi) : null) ?? detail.Turns.LastOrDefault();

        var blocks = new List<RoomBlock>();
        long cursor = 0;

        foreach (var turn in detail.Turns)
        {
            if (turn.UserMessage is { Length: > 0 } message)
                blocks.Add(new UserMessageBlock { Id = $"turn-{turn.TurnIndex}:user", Seq = 0, Text = message, At = turn.CreatedDate });

            // Project EVERY turn richly so each one's full execution UI is available on expand. The focused turn honours
            // the requested attempt (anchorRunId); every other turn focuses its own latest run (anchor null → the latest).
            // A non-focused TERMINAL turn's heavy flow never changes (a rerun mints a new run id), so serve it from the
            // cache — this is what keeps a multi-turn room from re-reading every past turn on each 2s poll. Its cheap
            // attempt ladder may still grow without changing the effective run/cache key, so overlay that from the fresh
            // SessionTurn. The focused turn (often the live one, or a chosen attempt) is always projected fresh.
            var isFocused = focused != null && turn.TurnIndex == focused.TurnIndex;
            var assistant = !isFocused && WorkflowRunState.IsTerminal(turn.RunStatus)
                ? await _cache.GetOrAddRoomAsync(turn.RunId, () => BuildTurnAsync(turn, null, teamId, cancellationToken)).ConfigureAwait(false)
                : await BuildTurnAsync(turn, isFocused ? anchorRunId : null, teamId, cancellationToken).ConfigureAwait(false);
            if (!isFocused && WorkflowRunState.IsTerminal(turn.RunStatus))
                assistant = assistant with { Attempts = AttemptsOf(turn, assistant.RunId) };

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

    private async Task<AssistantTurnBlock> BuildTurnAsync(SessionTurn turn, Guid? anchorRunId, Guid teamId, CancellationToken cancellationToken)
    {
        // Focus the REQUESTED attempt (the anchor run), so the header switcher can show ANY attempt's whole flow — not
        // always the latest. A prior attempt carries its own status / error / timing (the turn skeleton has the latest's).
        var focus = await FocusAsync(turn, anchorRunId, teamId, cancellationToken).ConfigureAwait(false);
        var runId = focus.RunId;

        // Per-ATTEMPT view: scope the node/agent phases STRICTLY to THIS run's own cells (mergeLineage: false). The
        // lineage-merged default shows each cell's LATEST attempt, so a FULL rerun (every attempt re-ran every cell)
        // would collapse every attempt to the latest's nodes — the switcher would show identical agents/tools for all.
        var phases = await _phases.ProjectAsync(runId, teamId, cancellationToken, mergeLineage: false).ConfigureAwait(false) ?? Array.Empty<RunPhase>();
        var watermark = await WatermarkAsync(runId, cancellationToken).ConfigureAwait(false);

        // Skip the pending-decision read entirely in the common case — the turn skeleton already knows whether this
        // run is parked on one (computed over both park backends), so a non-waiting turn pays zero query + zero parse.
        var decisions = turn.HasPendingDecision && focus.IsLatest
            ? await DecisionBlocksAsync(runId, teamId, watermark, cancellationToken).ConfigureAwait(false)
            : Array.Empty<DecisionBlock>();

        var facts = await GatherFactsAsync(runId, teamId, phases, focus.Status, focus.Error, cancellationToken).ConfigureAwait(false);

        var narrative = RoomNarrative.Build($"turn-{turn.TurnIndex}", watermark, phases, focus.Status, focus.Error, decisions, facts);

        var publish = await PublishStateAsync(runId, teamId, focus.Status, cancellationToken).ConfigureAwait(false);

        return new AssistantTurnBlock
        {
            Id = $"turn-{turn.TurnIndex}",
            Seq = watermark,
            TurnIndex = turn.TurnIndex,
            TurnRunId = turn.TurnRunId,
            RunId = runId,
            Status = focus.Status,
            // Fall back to the turn's own recorded result when the narrative has none — a turn with a result but sparse
            // execution records would otherwise show a blank lead (matches the journal projector + the prior light card).
            Summary = narrative.Summary ?? (focus.IsLatest && turn.Result is { Length: > 0 } r ? r : null),
            Map = narrative.Map,
            Blocks = narrative.Blocks,
            Actions = _actions.ResolveTurnActions(runId, focus.Status, publish),
            At = focus.CreatedDate,
            DurationMs = DurationOf(focus.CreatedDate, focus.StartedAt, focus.CompletedAt),
            Attempts = AttemptsOf(turn, runId),
        };
    }

    /// <summary>
    /// PR-6's gating signal for <see cref="RoomActionKind.OpenPullRequest"/> — null (button omitted) for a
    /// non-terminal run, so a running turn pays zero extra reads. Reads the SAME durable facts
    /// <see cref="IRoomPullRequestService"/> itself opens a PR off (<see cref="ISupervisorPublishedBranchResolver"/>,
    /// DC-3 — merge-derived OR ledger-direct), so "can I open one" and "what does opening one actually do" can
    /// never drift.
    /// </summary>
    private async Task<RoomPublishState?> PublishStateAsync(Guid runId, Guid teamId, Messages.Enums.WorkflowRunStatus status, CancellationToken cancellationToken)
    {
        if (!WorkflowRunState.IsTerminal(status)) return null;

        var priorDecisions = await ReadTerminalDecisionsAsync(runId, teamId, cancellationToken).ConfigureAwait(false);

        var branches = await _publishedBranches.ResolveAsync(runId, teamId, priorDecisions, primaryRepositoryId: null, cancellationToken).ConfigureAwait(false);

        if (branches.Count == 0) return new RoomPublishState { HasPublishedBranch = false };

        var manifests = await _manifests.ListForWorkflowRunAsync(runId, teamId, cancellationToken).ConfigureAwait(false);
        var openedUrl = manifests.FirstOrDefault(m => m.Kind == PublishManifestKind.Integration && m.PullRequestUrl is { Length: > 0 })?.PullRequestUrl;

        return new RoomPublishState { HasPublishedBranch = true, OpenedPullRequestUrl = openedUrl };
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

    /// <summary>
    /// Gather the focused turn's facts from the substrate — one decision-tape read (subtasks · changed files ·
    /// acceptance), one batched tool-count, one reasoning COUNT (never the text), and the PR node-join. All scoped to
    /// this run / its agents, so the cost scales with the turn, not the database. The pure narrative engine consumes these.
    /// </summary>
    private async Task<RoomTurnFacts> GatherFactsAsync(Guid runId, Guid teamId, IReadOnlyList<RunPhase> phases, Messages.Enums.WorkflowRunStatus status, string? error, CancellationToken cancellationToken)
    {
        var decisions = await _decisionObservations.GetForRunAsync(runId, teamId, cancellationToken).ConfigureAwait(false);

        // The supervisor rounds, segmented on each Plan (a re-plan opens a new round) — the render source (never lumped).
        var rounds = RoomRounds.Segment(decisions);

        var plan = decisions.LastOrDefault(d => d.DecisionKind == SupervisorDecisionKinds.Plan);
        var subtasks = SupervisorOutcome.ReadPlanSubtasks(plan?.PayloadJson).Select(s => s.Title).ToList();

        var agentIds = phases.SelectMany(p => p.Agents).Select(a => a.AgentRunId).Distinct().ToList();

        // The turn's ACTIVE-GENERATION agent results (latest fold per agent) — the one read drives the changed-file
        // list, the per-agent card summaries, and the lead fallback (no stop summary → compose from these). Earlier
        // plan generations remain in rounds as audit history, but cannot be repackaged as current final delivery.
        var agentResults = SupervisorPlanWindow.Read(decisions.Select(ToPriorDecision).ToList()).Decisions
            .Where(d => SupervisorDecisionKinds.StagesAgents(d.DecisionKind))
            .SelectMany(d => SupervisorOutcome.ReadAgentResults(d.OutcomeJson))
            .GroupBy(r => r.AgentRunId).Select(g => g.Last())
            .ToList();

        // A single-agent / non-supervisor run has an EMPTY decision tape, so the fold above is empty. Source its result
        // straight from the run's own AgentRun rows (the persisted AgentRunResult — summary + git-ground-truth changed
        // files) so a plain agent turn still shows a RESULT + its output, not just the execution dots.
        if (decisions.Count == 0 && agentIds.Count > 0)
            agentResults = await ReadAgentRunResultsAsync(agentIds, cancellationToken).ConfigureAwait(false);

        var changedFileIdentities = agentResults
            .SelectMany(FileIdentities)
            .DistinctBy(FileKey)
            .OrderBy(file => file.RepositoryAlias, StringComparer.Ordinal)
            .ThenBy(file => file.RepositoryId)
            .ThenBy(file => file.Path, StringComparer.Ordinal)
            .Take(MaxChangedFiles).ToList();
        var changedFiles = changedFileIdentities.Select(file => file.Path).ToList();

        var agentSummaries = agentResults
            .Where(r => !string.IsNullOrWhiteSpace(r.Summary))
            .ToDictionary(r => r.AgentRunId, r => r.Summary!.Trim());

        // Per-agent file attribution (B): each agent's OWN changed files, so a card shows WHICH agent produced a file
        // rather than the provenance-blind turn-level union. Bounded per agent; an agent that changed nothing is omitted.
        var agentFileIdentities = agentResults
            .Select(result => (result.AgentRunId, Files: (IReadOnlyList<RoomFileIdentity>)FileIdentities(result).Take(MaxAgentFiles).ToList()))
            .Where(result => result.Files.Count > 0)
            .ToDictionary(result => result.AgentRunId, result => result.Files);
        var agentFiles = agentFileIdentities.ToDictionary(pair => pair.Key, pair => (IReadOnlyList<string>)pair.Value.Select(file => file.Path).ToList());

        var stop = decisions.LastOrDefault(d => d.DecisionKind == SupervisorDecisionKinds.Stop);
        var acceptance = SupervisorOutcome.ReadAcceptanceGradePassed(stop?.OutcomeJson);

        // A stop is a clean terminal Success at the ENGINE level even when the run did NOT finish well — a fail-closed
        // model GIVE-UP (no-decision / no-model / unknown-decision), OR a SERVER-FORCED stop (a budget / governance /
        // bound trip stamping a {reason} with no outcome). Classify BOTH shapes through the ONE shared classifier the
        // Journal ③ stop step also reads, so the RESULT card renders DEGRADED (not a green success) for either — and the
        // step + the terminal can never drift. Generic: never a per-kind string. A THIRD shape — an orderly stop whose
        // objective acceptance grade FAILED — degrades the card too; see <see cref="ResultVerdict"/>.
        var stopClass = SupervisorOutcome.ClassifyStop(stop?.PayloadJson, stop?.OutcomeJson);
        var verdict = ResultVerdict(acceptance, stopClass);

        // The delivered answer text: the supervisor's closing line (or, for a forced stop, WHY it stopped — "budget
        // exhausted" — so the RESULT never renders blank), else — for a single-agent run with no supervisor — that one
        // agent's own final summary (its result IS the answer). A multi-agent run without a supervisor falls to the files.
        var finalAnswerText = stopClass.DisplayText
            ?? (decisions.Count == 0 && agentResults.Count == 1 ? agentResults[0].Summary : null);

        // The retry beats — one per retry decision, in tape order — each carrying its FRESH agent so the room renders that
        // agent's own "Retry" card chronologically. A no-op retry (nothing staged) carries no agent. (The retry's line +
        // rationale live on the Journal ③ beat now.)
        var retrySteps = decisions
            .Where(d => d.DecisionKind == SupervisorDecisionKinds.Retry)
            .OrderBy(d => d.Sequence)
            .Select(d =>
            {
                var agentId = SupervisorOutcome.ReadStagedAgentRunIds(d.OutcomeJson).FirstOrDefault();

                return new RoomRetryStep(d.Sequence, agentId == Guid.Empty ? null : agentId);
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
        // it produced noisy pseudo-"tools"). One bounded metadata fetch followed by bounded, tolerant prefix reads for
        // the uncommon offloaded carriers; inline payloads retain the exact existing path.
        var toolPayloads = agentIds.Count == 0 ? new List<ToolPayload>() : await _db.AgentRunEvent.AsNoTracking()
            .Where(e => agentIds.Contains(e.AgentRunId) && e.Kind == AgentEventKind.ToolCall)
            .OrderBy(e => e.Sequence)
            .Select(e => new ToolPayload(e.DataJson, e.DataArtifactId))
            .Take(MaxToolScan)
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        var toolNames = await ToolNamesAsync(toolPayloads, teamId, cancellationToken).ConfigureAwait(false);
        var toolHistogram = toolNames
            .GroupBy(name => name)
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

        var delivery = await DeliveryAsync(runId, teamId, cancellationToken).ConfigureAwait(false);

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
            FinalAnswer = BuildFinalAnswer(finalAnswerText, changedFileIdentities, delivery, verdict,
                await VerificationOf(runId, status, verdict, acceptance, SupervisorOutcome.ReadAcceptanceGradeJudgedSummary(stop?.OutcomeJson), agentResults, cancellationToken).ConfigureAwait(false)),
            LatestLines = latestLines,
            AgentFiles = agentFiles,
            AgentFileIdentities = agentFileIdentities,
            Subtasks = subtasks,
            ChangedFiles = changedFiles,
            ChangedFileIdentities = changedFileIdentities,
            Deliverables = await DeliverablesAsync(runId, teamId, cancellationToken).ConfigureAwait(false),
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
            NetworkPosture = await NetworkPostureAsync(runId, teamId, cancellationToken).ConfigureAwait(false),
        };
    }

    /// <summary>
    /// The run's effective network posture, read from the two columns that record it — the launch-stamped route
    /// provenance (<c>route_plan_jsonb</c>: the resolved tier plus the ceiling it was clamped to) for what was
    /// ASKED FOR, and the run's agents' <c>sandbox_confinement</c> for what the host actually DID. Narrow column
    /// projections, never the frozen definition graph. Null (no row rendered) for a run with no route provenance, or
    /// one staged before the launch stamped its resolved tier: an unknown posture is left UNSAID rather than guessed
    /// as "off", since a wrong "off" is exactly the silent claim this row exists to end. A run whose agents recorded
    /// no confinement (launched before the stamp existed) keeps the hedged wording — the same reason.
    /// </summary>
    private async Task<string?> NetworkPostureAsync(Guid runId, Guid teamId, CancellationToken cancellationToken)
    {
        var json = await _db.WorkflowRun.AsNoTracking()
            .Where(r => r.Id == runId && r.TeamId == teamId)
            .Select(r => r.RoutePlanJson)
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(json)) return null;

        var route = TryReadRoute(json);

        if (route is null || route.EffectiveAutonomy.Length == 0) return null;

        return AgentAutonomyPolicy.DescribeNetwork(
            AgentAutonomyPolicy.Parse(route.EffectiveAutonomy, AgentAutonomyLevel.Standard),
            AgentAutonomyPolicy.Parse(route.Caps.AutonomyCeiling, AgentAutonomyLevel.Unleashed),
            await ConfinementAsync(runId, teamId, cancellationToken).ConfigureAwait(false));
    }

    /// <summary>
    /// The posture this turn's agents actually ran under, folded to ONE record by <see cref="LeastConfined"/>. Null
    /// when no agent recorded one (an older run, or a turn that spawned no agent) — the caller then keeps the hedge.
    /// </summary>
    private async Task<SandboxConfinement?> ConfinementAsync(Guid runId, Guid teamId, CancellationToken cancellationToken)
    {
        var records = await _db.AgentRun.AsNoTracking()
            .Where(r => r.WorkflowRunId == runId && r.TeamId == teamId && r.SandboxConfinementJson != null)
            .Select(r => r.SandboxConfinementJson!)
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        return LeastConfined(records);
    }

    /// <summary>
    /// Fold a turn's agent-run confinement records into the ONE the sentence may claim: the LEAST confined of them.
    /// A turn's agents can land on different workers, so "some were confined" must never be rendered as "this turn
    /// was confined" — the reader's question is whether ANY of these agents could reach the network, and one
    /// unconfined agent answers it yes. Unparseable rows are skipped (a malformed column drops the resolution back to
    /// the hedge, never fails a turn); an all-unparseable set therefore reads as no record at all.
    ///
    /// <para>The rows arrive from an UNORDERED query and the rank ties records that print DIFFERENT causes (an
    /// unconfined host vs a runner that confines nothing; two hosts that hit different walls), so the reason breaks
    /// the tie — otherwise one turn reads two ways across two page loads. Ordinal, so the pick is the same on every
    /// host's culture.</para>
    /// </summary>
    internal static SandboxConfinement? LeastConfined(IEnumerable<string> json) =>
        json.Select(TryReadConfinement).OfType<SandboxConfinement>()
            .OrderBy(ConfinementRank).ThenBy(c => c.Reason ?? "", StringComparer.Ordinal)
            .FirstOrDefault();

    /// <summary>Ascending strength: anything not confined is 0 (the reader's "yes, one of them could reach the network"), confinement without a severed netns 1, full severance 2.</summary>
    private static int ConfinementRank(SandboxConfinement confinement) =>
        confinement.Outcome != SandboxConfinementOutcome.Confined ? 0 : confinement.NetworkSevered ? 2 : 1;

    /// <summary>Deserialize one confinement record with the SAME options the executor wrote it with; a malformed / legacy column degrades to null.</summary>
    private static SandboxConfinement? TryReadConfinement(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<SandboxConfinement>(json, AgentJson.Options);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Deserialize the stamped route provenance with the SAME web options <c>TaskRunSnapshotFactory</c> wrote it with; a malformed / legacy column degrades to null (the room drops one row, never fails a turn).</summary>
    private static Messages.Tasks.RoutePlan? TryReadRoute(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<Messages.Tasks.RoutePlan>(json, RouteJson);
        }
        catch (JsonException)
        {
            return null;
        }
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

    /// <summary>
    /// A1 (result honesty) — the RESULT card's verdict, composed from the SAME two durable stop-decision facts the
    /// engine folds into the run row's <c>Outcome</c> through <see cref="SupervisorOutcome.HonestOutcomeOf"/>: the
    /// objective acceptance grade and the stop's classification. Reusing that one authority is what makes the card and
    /// the run row's word un-driftable — a run whose checks FAILED can never render the green Result behind the
    /// model's own success-sounding closing line.
    ///
    /// <para>The reason is stated only when the card's TEXT does not already carry it: a give-up / forced stop's text
    /// IS the classifier's account of why it stopped, whereas a failed grade leaves the model's closing line intact,
    /// so the verdict needs its own line. Copy is authored here (Rule: the backend owns the room's words).</para>
    ///
    /// <para>Pure; internal so it is unit-pinned directly (InternalsVisibleTo) rather than only through the DB tier.</para>
    /// </summary>
    internal static (bool Degraded, string? Reason) ResultVerdict(bool? acceptancePassed, SupervisorStopClassification stopClass)
    {
        var acceptanceFailed = SupervisorOutcome.HonestOutcomeOf(acceptancePassed, stopClass) == SupervisorOutcome.AcceptanceFailedOutcome;

        return (acceptanceFailed || stopClass.Degraded, acceptanceFailed ? AcceptanceFailedReason : null);
    }

    /// <summary>The card's account of a FAILED objective acceptance grade — the same word the Runs list already uses for the <c>AcceptanceFailed</c> outcome, so the two surfaces read alike.</summary>
    private const string AcceptanceFailedReason = "Checks failed";

    /// <summary>The unverified chip's copy — backend-authored, so the FE never maps a flag to words.</summary>
    internal const string UnverifiedNote = "Unverified — no check ran on this result";

    /// <summary>The chip's copy for a stop whose only grade read the model's own closing PROSE. A real verdict, but not one that examined a result — so the card says which it was rather than claiming the stronger thing.</summary>
    internal const string SummaryJudgedNote = "Unverified — judged from the stop summary";

    /// <summary>
    /// The containment probe for an interaction record written by the OUTPUT critic — <c>payload_json @&gt;
    /// '{"kind":"critic.output"}'</c>. Built off the critic's own <c>OutputReviewCallKind</c> const so a rename cannot
    /// silently stop finding its reviews.
    ///
    /// <para>It probes the OUTPUT kind alone, never the generic <c>critic.review</c>: the plan critic and the decision
    /// critic record under that same generic label, so a supervisor run whose only review examined a DECISION would
    /// otherwise claim the run's RESULT was verified with nothing having read it — the exact silence this marker exists
    /// to end, restated one rung up.</para>
    /// </summary>
    private static readonly string CriticOutputReviewProbe = JsonSerializer.Serialize(new Dictionary<string, string> { ["kind"] = Review.LlmStructuredCritic.OutputReviewCallKind });

    /// <summary>
    /// C1 — whether ANY check examined this result. A run can terminalize a green Success having been graded by
    /// nothing at all: no operator floor, no model-authored acceptance, no output critic. That card used to read
    /// exactly like a fully-verified one, which is the most expensive silence in the room — so it now says so.
    ///
    /// <para>Verification is claimed from three independent facts, cheapest first: the STOP's acceptance grade, any
    /// UNIT's acceptance grade (both already in hand), and — only when neither exists — ONE bounded ledger probe for a
    /// recorded critic review. The probe is the only way an APPROVED critic leaves a trace (a flag or a skip writes a
    /// warning; a clean pass writes nothing but its interaction row), and it is paid only on the ungraded-Success path,
    /// so every verified run and every live turn costs zero extra query.</para>
    ///
    /// <para>A stop grade the model reached by judging its OWN closing prose (C1's summary fallback, marked
    /// <c>judgedSummary</c> on the tape) does not count as having examined a result: the model's account of its work is
    /// not evidence about the work. Such a card is unverified with its own copy. This changes only the chip — the run's
    /// <c>Solved</c> / acceptance outcome is the grade's, exactly as before.</para>
    ///
    /// <para>Null when the question does not arise — a non-Success or an already-degraded card carries its own account
    /// and must not gain a second, competing one.</para>
    /// </summary>
    private async Task<(bool? Verified, string? Note)> VerificationOf(Guid runId, Messages.Enums.WorkflowRunStatus status, (bool Degraded, string? Reason) verdict, bool? acceptance, bool judgedSummary, IReadOnlyList<Messages.Agents.SupervisorAgentResult> agentResults, CancellationToken cancellationToken)
    {
        if (status != Messages.Enums.WorkflowRunStatus.Success || verdict.Degraded) return (null, null);

        var graded = (acceptance is not null && !judgedSummary) || agentResults.Any(r => r.AcceptancePassed is not null);

        return Verification(graded, graded || await CriticReviewedAsync(runId, cancellationToken).ConfigureAwait(false), judgedSummary);
    }

    /// <summary>The pure half of <see cref="VerificationOf"/> — pinned directly so the claim "something checked this" can never be widened by accident.</summary>
    internal static (bool? Verified, string? Note) Verification(bool graded, bool criticReviewed, bool judgedSummary = false) =>
        graded || criticReviewed ? (true, null) : (false, judgedSummary ? SummaryJudgedNote : UnverifiedNote);

    /// <summary>Whether a model critic ever recorded an OUTPUT review for this run — the ONLY durable trace an APPROVED verdict leaves (a flag or a skip writes its own warning; a clean pass writes nothing but this interaction row).</summary>
    private async Task<bool> CriticReviewedAsync(Guid runId, CancellationToken cancellationToken) =>
        await _db.WorkflowRunRecord.AsNoTracking()
            .Where(r => r.RunId == runId && r.RecordType == WorkflowRunRecordTypes.InteractionCompleted)
            .AnyAsync(r => EF.Functions.JsonContains(r.PayloadJson, CriticOutputReviewProbe), cancellationToken).ConfigureAwait(false);

    /// <summary>The rich final answer — the stop summary text + typed attachments (the changed files + the PR). Images are a true gap (no run output exposes them). Null when there's nothing to deliver. <paramref name="verdict"/> marks a stop that did NOT finish well (a give-up / forced stop, or a failed acceptance grade) so the card renders neutral, not a green success.</summary>
    private static RoomFinalAnswer? BuildFinalAnswer(string? text, IReadOnlyList<RoomFileIdentity> files, RoomDelivery? pr, (bool Degraded, string? Reason) verdict, (bool? Verified, string? Note) verification)
    {
        var attachments = new List<RoomAttachment>();

        foreach (var file in files.Take(MaxAnswerFiles))
            attachments.Add(new RoomAttachment(AnswerAttachmentKind.FileLink, file.Path, Url: null, PreviewUrl: null, DownloadUrl: null, File: file));

        if (pr is { } d)
            attachments.Add(new RoomAttachment(AnswerAttachmentKind.Pr, d.Reference is { Length: > 0 } r ? $"{d.Title} {r}" : d.Title, Url: d.Url, PreviewUrl: null, DownloadUrl: null));

        var body = string.IsNullOrWhiteSpace(text) ? null : text.Trim();

        return body == null && attachments.Count == 0 ? null : new RoomFinalAnswer { Text = body, Attachments = attachments, Degraded = verdict.Degraded, DegradedReason = verdict.Reason, Verified = verification.Verified, VerificationNote = verification.Note };
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
            .Select(r => new { r.Id, r.Status, r.Error, r.ResultJson })
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        return rows.Select(r => SupervisorOutcome.ProjectCompact(r.Id, r.Status.ToString(), r.Error, r.ResultJson)).ToList();
    }

    private static SupervisorPriorDecision ToPriorDecision(SupervisorDecisionRecord decision) => new()
    {
        Id = decision.Id,
        Sequence = decision.Sequence,
        DecisionKind = decision.DecisionKind,
        Status = decision.Status,
        PayloadJson = decision.PayloadJson,
        OutcomeJson = decision.OutcomeJson,
        Error = decision.Error,
    };

    /// <summary>
    /// The files this run produced as files, current copies only.
    ///
    /// <para>Superseded rows are excluded here rather than in the store: the ledger is append-only and a superseded
    /// row pointing at its successor is exactly what makes a re-capture auditable, so a reader that wants "what did
    /// this run produce" filters, and a reader that wants the chain still has it.</para>
    /// </summary>
    private async Task<IReadOnlyList<DeliverableFile>> DeliverablesAsync(Guid runId, Guid teamId, CancellationToken cancellationToken) =>
        (await _producedFiles.ListForWorkflowRunAsync(runId, teamId, cancellationToken).ConfigureAwait(false))
            .Where(manifest => manifest.SupersededByManifestId == null)
            .Take(MaxChangedFiles)
            .Select(manifest => new DeliverableFile
            {
                Path = manifest.LogicalPath,
                Kind = manifest.Kind.ToString(),
                SizeBytes = manifest.SizeBytes,
                ContentType = manifest.ContentType,
                ArtifactId = manifest.ContentArtifactId,
                AgentRunId = manifest.AgentRunId,
            })
            .ToList();

    private async Task<IReadOnlyList<SupervisorPriorDecision>> ReadTerminalDecisionsAsync(Guid runId, Guid teamId, CancellationToken cancellationToken)
    {
        var decisions = await _decisionObservations.GetForRunAsync(runId, teamId, cancellationToken).ConfigureAwait(false);
        return decisions.Where(decision => SupervisorDecisionStateMachine.IsTerminal(decision.Status)).Select(ToPriorDecision).ToList();
    }

    /// <summary>Project one compact result into clickable file identities. Per-repository results are authoritative whenever present; top-level paths are the legacy/single-repo fallback only.</summary>
    private static IEnumerable<RoomFileIdentity> FileIdentities(SupervisorAgentResult result)
    {
        if (result.RepositoryResults.Count > 0)
        {
            foreach (var repository in result.RepositoryResults)
                foreach (var path in repository.ChangedFiles)
                    yield return new RoomFileIdentity { Path = path, AgentRunId = result.AgentRunId, RepositoryId = repository.RepositoryId, RepositoryAlias = repository.Alias };

            yield break;
        }

        foreach (var path in result.ChangedFiles)
            yield return new RoomFileIdentity { Path = path, AgentRunId = result.AgentRunId };
    }

    /// <summary>A file is unique by repository + repo-relative path. AgentRunId remains on the selected identity so the click resolves the exact producing attempt.</summary>
    private static (Guid? RepositoryId, string? RepositoryAlias, string Path) FileKey(RoomFileIdentity file) =>
        (file.RepositoryId, file.RepositoryId is null ? file.RepositoryAlias : null, file.Path);

    /// <summary>The web defaults <c>TaskRunSnapshotFactory</c> stamped <c>route_plan_jsonb</c> with — the read side must match the write side or every posture row silently disappears.</summary>
    private static readonly JsonSerializerOptions RouteJson = new(JsonSerializerDefaults.Web);

    private const int MaxChangedFiles = 200;
    private const int MaxAgentFiles = 40;
    private const int MaxReasoningSteps = 40;
    private const int MaxLatestLineScan = 200;
    private const int MaxAnswerFiles = 40;
    private const int MaxToolScan = 2000;
    private const int MaxToolArtifactHydrates = 128;
    private const int MaxToolPayloadPrefixBytes = 16 * 1024;
    private const int MaxFailureScan = 50;

    /// <summary>
    /// Resolve the narrow <c>data.name</c> display fact without loading whole large payloads. At most 128 distinct
    /// artifacts × 16 KiB are inspected per turn; CAS duplicates share one read. Missing/corrupt/backend-unavailable
    /// UI data remains an explicit typed display bucket rather than failing the room or dropping the call.
    /// </summary>
    private async Task<List<string>> ToolNamesAsync(IReadOnlyList<ToolPayload> payloads, Guid teamId, CancellationToken cancellationToken)
    {
        var names = new List<string>(payloads.Count);
        var artifactIds = payloads
            .Where(payload => payload.DataJson is null && payload.DataArtifactId is not null)
            .Select(payload => payload.DataArtifactId!.Value)
            .Distinct().Take(MaxToolArtifactHydrates).ToArray();
        var artifactReads = artifactIds.Length == 0
            ? new Dictionary<Guid, ArtifactRangeReadResult>()
            : await _artifacts.ReadRangesAsync(new ArtifactRangesReadRequest(teamId, artifactIds, 0, MaxToolPayloadPrefixBytes), cancellationToken).ConfigureAwait(false);
        var artifactNames = new Dictionary<Guid, string>();

        foreach (var payload in payloads)
        {
            if (payload.DataJson is { } inline)
            {
                names.Add(ToolName(inline));
                continue;
            }

            if (payload.DataArtifactId is not { } artifactId)
            {
                names.Add("tool");
                continue;
            }

            if (!artifactNames.TryGetValue(artifactId, out var name))
            {
                name = artifactReads.TryGetValue(artifactId, out var read)
                    ? read.State == ArtifactRangeReadState.Available ? ToolName(read.Bytes!) : UnavailableToolName(read.State)
                    : "tool (payload not inspected)";
                artifactNames.Add(artifactId, name);
            }

            names.Add(name);
        }

        return names;
    }

    private static string UnavailableToolName(ArtifactRangeReadState state) => state switch
    {
        ArtifactRangeReadState.MetadataMissing or ArtifactRangeReadState.PhysicalObjectMissing => "tool (payload missing)",
        ArtifactRangeReadState.IntegrityFailure => "tool (payload corrupt)",
        ArtifactRangeReadState.BackendUnavailable or ArtifactRangeReadState.AccessDenied => "tool (payload unavailable)",
        _ => "tool (payload unavailable)",
    };

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

    /// <summary>Streaming prefix parser for an offloaded JSON object; <c>isFinalBlock: false</c> deliberately accepts a bounded prefix without requiring the entire large document.</summary>
    private static string ToolName(ReadOnlySpan<byte> utf8Json)
    {
        if (utf8Json.IsEmpty) return "tool";

        try
        {
            var reader = new Utf8JsonReader(utf8Json, isFinalBlock: false, state: default);
            while (reader.Read())
            {
                if (reader.TokenType != JsonTokenType.PropertyName || reader.CurrentDepth != 1 || !reader.ValueTextEquals("name")) continue;
                if (!reader.Read() || reader.TokenType != JsonTokenType.String) return "tool";
                return reader.GetString() is { Length: > 0 } name ? name : "tool";
            }
        }
        catch (JsonException) { }

        return "tool";
    }

    private sealed record ToolPayload(string? DataJson, Guid? DataArtifactId);

    /// <summary>The PR the turn opened, joined from the run's open-PR node output (number/url) + its inputs (title / branches) — OR, DC-3, a fallback onto <c>PublishManifest</c> for a PR opened OUTSIDE any workflow node (the Room's own Open-PR button, or a server-authored delivery step) that a pre-wired <c>git.open_pr</c> node never ran for. Null when the turn opened none either way.</summary>
    private async Task<RoomDelivery?> DeliveryAsync(Guid runId, Guid teamId, CancellationToken cancellationToken)
    {
        var nodes = await _db.WorkflowRunNode.AsNoTracking()
            .Where(n => n.RunId == runId)
            .Select(n => new { n.OutputsJson, n.InputsJson })
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        return nodes.Select(n => RoomDeliveryParser.Parse(n.OutputsJson, n.InputsJson)).FirstOrDefault(d => d != null)
            ?? await DeliveryFromManifestAsync(runId, teamId, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Joins the resolver's branch (for head/base) with the manifest's own PR reference (number/url), by alias — the ONE non-node-output source of "did this run open a PR" the card can show.</summary>
    private async Task<RoomDelivery?> DeliveryFromManifestAsync(Guid runId, Guid teamId, CancellationToken cancellationToken)
    {
        var manifests = await _manifests.ListForWorkflowRunAsync(runId, teamId, cancellationToken).ConfigureAwait(false);
        var opened = manifests.FirstOrDefault(m => m.Kind == PublishManifestKind.Integration && m.PullRequestUrl is { Length: > 0 });

        if (opened is null) return null;

        var priorDecisions = await ReadTerminalDecisionsAsync(runId, teamId, cancellationToken).ConfigureAwait(false);
        var branches = await _publishedBranches.ResolveAsync(runId, teamId, priorDecisions, primaryRepositoryId: null, cancellationToken).ConfigureAwait(false);
        var branch = branches.FirstOrDefault(b => b.Alias == opened.RepositoryAlias);

        return new RoomDelivery
        {
            Title = $"Pull request #{opened.PullRequestNumber}",
            Reference = opened.PullRequestNumber is { } n ? $"#{n}" : null,
            BranchHead = branch?.SourceBranch ?? opened.Branch,
            BranchBase = branch?.TargetBranch,
            Url = opened.PullRequestUrl,
        };
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
