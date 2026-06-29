using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Tasks.Phases.Sources.Nodes;
using CodeSpace.Core.Services.Tasks.Phases.Sources.Supervisor;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Dtos.Sessions.Room;
using CodeSpace.Messages.Enums;
using CodeSpace.Messages.Tasks.Phases;

namespace CodeSpace.Core.Services.Sessions.Room;

/// <summary>
/// The PURE narrative engine — turns a run's merged phase list + the projector-gathered <see cref="RoomTurnFacts"/>
/// into the render-ready turn blocks the Session Room design shows: the canonical Plan→Work→Review→Deliver execution
/// map, the stat rows (subtasks / files / tools / reasoning), the delivery (PR) card, the pending-decision card, and
/// the rich failure diagnostic. The backend OWNS copy + order; the frontend renders by block type. No DB, no I/O —
/// the projector does the (bounded, focused-turn) reads and hands the facts in; this stays unit-tested exhaustively.
/// </summary>
public static class RoomNarrative
{
    /// <summary>The map + summary + inner blocks for one turn — everything <see cref="AssistantTurnBlock"/> needs below its header.</summary>
    public sealed record TurnNarrative(string? Summary, ExecutionMapBlock? Map, IReadOnlyList<RoomBlock> Blocks);

    public static TurnNarrative Build(string idPrefix, long seq, IReadOnlyList<RunPhase> phases, WorkflowRunStatus status, string? error, IReadOnlyList<DecisionBlock> decisions, RoomTurnFacts facts)
    {
        var tape = phases.Where(p => p.SourceKey == SupervisorPhaseSource.Key && p.Kind != SupervisorPhaseSource.AuthoredPhaseKind).OrderBy(p => p.Order).ToList();
        var structural = phases.Where(p => p.SourceKey == WorkflowNodePhaseSource.Key).OrderBy(p => p.Order).ToList();

        // A supervisor turn maps to the canonical lifecycle; a non-supervisor run keeps its structural node spine.
        var map = tape.Count > 0
            ? BuildCanonicalMap(idPrefix, seq, tape, status, facts.AcceptancePassed)
            : BuildMap(idPrefix, seq, structural);

        var blocks = BuildBlocks(idPrefix, seq, status, error, decisions, facts, tape.Count > 0 ? tape : structural);

        return new TurnNarrative(SummaryFor(status, tape, structural, error, facts), map, blocks);
    }

    // ─── execution map ───────────────────────────────────────────────────────────

    /// <summary>
    /// The canonical supervisor lifecycle as the execution map — Plan → Work → Review → Deliver — derived from the
    /// decision tape + the run status (and the objective acceptance verdict for Review). The model's verbs fold onto
    /// fixed stages; per-step detail is the plan duration, the agent count / live progress, and the review / deliver
    /// outcome. The stage labels are the lifecycle vocabulary, not per-run copy.
    /// </summary>
    private static ExecutionMapBlock BuildCanonicalMap(string idPrefix, long seq, IReadOnlyList<RunPhase> tape, WorkflowRunStatus status, bool? acceptancePassed)
    {
        var plan = tape.FirstOrDefault(p => p.Kind == SupervisorDecisionKinds.Plan);
        var agents = tape.Where(p => SupervisorDecisionKinds.StagesAgents(p.Kind)).SelectMany(p => p.Agents).ToList();

        var active = status is WorkflowRunStatus.Pending or WorkflowRunStatus.Enqueued or WorkflowRunStatus.Running or WorkflowRunStatus.Suspended;
        var failed = status is WorkflowRunStatus.Failure or WorkflowRunStatus.Cancelled;
        var succeeded = status == WorkflowRunStatus.Success;

        var planStatus = plan != null ? ExecutionStepStatus.Done : active ? ExecutionStepStatus.Running : ExecutionStepStatus.Pending;
        var (workStatus, workDetail) = WorkStage(plan != null, agents, active);
        var (reviewStatus, reviewDetail) = ReviewStage(succeeded, failed, workStatus, acceptancePassed);
        var (deliverStatus, deliverDetail) = DeliverStage(succeeded, failed);

        var steps = new[]
        {
            Step($"{idPrefix}:plan", "Plan", planStatus, plan != null ? PhaseDuration(plan) : null),
            Step($"{idPrefix}:work", "Work", workStatus, workDetail),
            Step($"{idPrefix}:review", "Review", reviewStatus, reviewDetail),
            Step($"{idPrefix}:deliver", "Deliver", deliverStatus, deliverDetail),
        };

        return new ExecutionMapBlock { Id = $"{idPrefix}:map", Seq = seq, Steps = steps };
    }

    /// <summary>The fallback map for a non-supervisor run — the structural node spine, with the technical node labels humanized to plain lifecycle words ("Fan out" → "Work", "planner" → "Plan").</summary>
    private static ExecutionMapBlock? BuildMap(string idPrefix, long seq, IReadOnlyList<RunPhase> structural)
    {
        if (structural.Count == 0) return null;

        var steps = structural.Select((p, i) => Step($"{idPrefix}:step-{i}", HumanizeStage(p.Label), MapStatus(p.Status), null)).ToList();

        return new ExecutionMapBlock { Id = $"{idPrefix}:map", Seq = seq, Steps = steps };
    }

    /// <summary>Map a technical workflow node label to a plain lifecycle word the reader understands (a "fan out" node is "Work", a "planner" is "Plan"). Unknown labels pass through unchanged.</summary>
    private static string HumanizeStage(string label)
    {
        var l = label.ToLowerInvariant();

        if (l.Contains("fan")) return "Work";
        if (l.Contains("plan")) return "Plan";
        if (l is "start" or "begin") return "Start";
        if (l.Contains("review") || l.Contains("verify")) return "Review";
        if (l.Contains("deliver") || l.Contains("merge")) return "Deliver";

        return label;
    }

    private static ExecutionMapStep Step(string id, string label, ExecutionStepStatus status, string? detail) =>
        new() { Id = id, Label = label, Status = status, Detail = detail };

    /// <summary>Work folds from the spawned agents: none yet → queued (after a plan) / pending; any failed → Failed; any still active → Running "k of N"; all done → Done "N agents".</summary>
    private static (ExecutionStepStatus, string?) WorkStage(bool planned, IReadOnlyList<PhaseAgentRef> agents, bool active)
    {
        if (agents.Count == 0)
            return planned ? (active ? ExecutionStepStatus.Queued : ExecutionStepStatus.Skipped, active ? "queued" : null) : (ExecutionStepStatus.Pending, null);

        var done = agents.Count(a => a.Status == nameof(AgentRunStatus.Succeeded));
        var anyFailed = agents.Any(a => a.Status is nameof(AgentRunStatus.Failed) or nameof(AgentRunStatus.Cancelled) or nameof(AgentRunStatus.TimedOut));
        var anyActive = agents.Any(a => !IsAgentTerminal(a.Status));   // Queued / Running — NeedsReview is terminal, not active

        if (anyFailed) return (ExecutionStepStatus.Failed, "failed");
        if (anyActive) return (ExecutionStepStatus.Running, $"{done} of {agents.Count}");
        return (ExecutionStepStatus.Done, AgentWord(agents.Count));
    }

    /// <summary>Review folds from the objective acceptance verdict when graded, else from the run outcome: success → passed; failed-at-work → skipped; failed-at-review → failed; else queued / running once work is done.</summary>
    private static (ExecutionStepStatus, string?) ReviewStage(bool succeeded, bool failed, ExecutionStepStatus work, bool? acceptancePassed)
    {
        if (acceptancePassed is true) return (ExecutionStepStatus.Done, "passed");
        if (acceptancePassed is false) return (ExecutionStepStatus.Failed, "failed");

        if (succeeded) return (ExecutionStepStatus.Done, "passed");
        if (failed) return work == ExecutionStepStatus.Failed ? (ExecutionStepStatus.Skipped, "skipped") : (ExecutionStepStatus.Failed, "failed");
        return work == ExecutionStepStatus.Done ? (ExecutionStepStatus.Running, null) : (ExecutionStepStatus.Queued, "queued");
    }

    /// <summary>Deliver folds from the run outcome: success → Done (the PR reference rides the delivery card); failed → skipped; else queued.</summary>
    private static (ExecutionStepStatus, string?) DeliverStage(bool succeeded, bool failed)
    {
        if (succeeded) return (ExecutionStepStatus.Done, null);
        if (failed) return (ExecutionStepStatus.Skipped, "skipped");
        return (ExecutionStepStatus.Queued, "queued");
    }

    // ─── inner blocks ──────────────────────────────────────────────────────────────

    /// <summary>
    /// The turn's inner blocks in the design's render order — Plan (subtasks) → Agents → Files changed → Tools →
    /// Reasoning → delivery (PR) → pending decisions → failure diagnostic. Each is emitted only when it has something to
    /// say; large content (reasoning text, the per-call tool list) is fetched lazily on expand, never loaded here.
    /// </summary>
    private static IReadOnlyList<RoomBlock> BuildBlocks(string idPrefix, long seq, WorkflowRunStatus status, string? error, IReadOnlyList<DecisionBlock> decisions, RoomTurnFacts facts, IReadOnlyList<RunPhase> narrativePhases)
    {
        var blocks = new List<RoomBlock>();

        if (SubtasksStat(idPrefix, seq, facts) is { } subtasks) blocks.Add(subtasks);

        if (AgentGroup(idPrefix, seq, status, facts, narrativePhases) is { } agents) blocks.Add(agents);

        if (FilesStat(idPrefix, seq, facts) is { } files) blocks.Add(files);
        if (ToolsStat(idPrefix, seq, facts) is { } tools) blocks.Add(tools);
        if (ReasoningStat(idPrefix, seq, facts) is { } reasoning) blocks.Add(reasoning);

        if (DeliveryFrom(idPrefix, seq, facts) is { } delivery) blocks.Add(delivery);

        // Pending decisions are "now" — the current ask.
        blocks.AddRange(decisions);

        if (status is WorkflowRunStatus.Failure or WorkflowRunStatus.Cancelled)
            blocks.Add(RichDiagnostic(idPrefix, seq, status, error, facts, narrativePhases));

        return blocks;
    }

    // ─── stat rows (label · detail, design vocabulary) ──────────────────────────────

    private static StatBlock? SubtasksStat(string idPrefix, long seq, RoomTurnFacts f) =>
        f.Subtasks.Count == 0 ? null : new StatBlock { Id = $"{idPrefix}:stat:subtasks", Seq = seq, Kind = "subtasks", Label = "Plan", Detail = Count(f.Subtasks.Count, "subtask"), Items = f.Subtasks.Select(t => new StatItem { Text = t }).ToList() };

    private static StatBlock? FilesStat(string idPrefix, long seq, RoomTurnFacts f) =>
        f.ChangedFiles.Count == 0 ? null : new StatBlock { Id = $"{idPrefix}:stat:files", Seq = seq, Kind = "files", Label = "Files changed", Detail = FilesDetail(f.Additions, f.Deletions, f.ChangedFiles.Count), Items = f.ChangedFiles.Select(p => new StatItem { Text = p }).ToList() };

    private static StatBlock? ToolsStat(string idPrefix, long seq, RoomTurnFacts f) =>
        f.ToolCalls is not > 0 ? null : new StatBlock { Id = $"{idPrefix}:stat:tools", Seq = seq, Kind = "tools", Label = "Tools", Detail = ToolsDetail(f.ToolCalls.Value, f.ToolHistogram), Items = f.ToolHistogram.Select(h => new StatItem { Text = h.Kind, Detail = h.Count.ToString() }).ToList() };

    private static StatBlock? ReasoningStat(string idPrefix, long seq, RoomTurnFacts f) =>
        f.ReasoningCount == 0 ? null : new StatBlock { Id = $"{idPrefix}:stat:reasoning", Seq = seq, Kind = "reasoning", Label = "Reasoning", Detail = Count(f.ReasoningCount, "step"), Items = f.ReasoningSteps.Select(t => new StatItem { Text = t }).ToList() };

    /// <summary>"+148 −32 · 6 files" when the diff stat is captured, else just "6 files" (the line counts are a true gap → no "+X −Y").</summary>
    private static string FilesDetail(int? additions, int? deletions, int count)
    {
        var files = Count(count, "file");
        return DiffStat(additions, deletions) is { } diff ? $"{diff} · {files}" : files;
    }

    /// <summary>"14 calls · read, edit, test" — the count plus the top tool kinds, or just the count when the histogram is empty.</summary>
    private static string ToolsDetail(int calls, IReadOnlyList<ToolKindCount> histogram) =>
        histogram.Count == 0 ? Count(calls, "call") : $"{Count(calls, "call")} · {string.Join(", ", histogram.Take(4).Select(h => h.Kind))}";

    private static string? DiffStat(int? additions, int? deletions) =>
        additions is { } a && deletions is { } d ? $"+{a} −{d}" : null;   // "+148 −32" (U+2212 minus); null until the diff stat is captured

    // ─── agents ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The turn's agents as one group — the design's "Agents · N ran in parallel" (collapsed) / "Work · N agents"
    /// (live) section. Folds EVERY phase that carries agents (a supervisor spawn AND a flow.map fan-out), with the
    /// already-projected <see cref="PhaseAgentRef"/> meta (zero extra read) + the per-agent result summary the projector
    /// gathered. Null below 2 agents — the group represents PARALLEL work; a single-agent turn IS that agent (no card).
    /// </summary>
    private static AgentGroupBlock? AgentGroup(string idPrefix, long seq, WorkflowRunStatus status, RoomTurnFacts facts, IReadOnlyList<RunPhase> phases)
    {
        var agents = phases.SelectMany(p => p.Agents)
            .GroupBy(a => a.AgentRunId).Select(g => g.First()).ToList();

        if (agents.Count < 2) return null;

        var active = status is WorkflowRunStatus.Pending or WorkflowRunStatus.Enqueued or WorkflowRunStatus.Running or WorkflowRunStatus.Suspended;

        return new AgentGroupBlock
        {
            Id = $"{idPrefix}:agents", Seq = seq,
            Title = active ? "Work" : "Agents",
            Agents = agents.Select(a => ToCard(a, facts.AgentSummaries)).ToList(),
        };
    }

    private static RoomAgentCard ToCard(PhaseAgentRef a, IReadOnlyDictionary<Guid, string> summaries) => new()
    {
        AgentRunId = a.AgentRunId,
        Label = a.Role ?? a.AssignedSubtask ?? a.Goal ?? a.Label ?? "Agent",
        Role = a.Role,
        Status = a.Status,
        AssignedSubtask = a.AssignedSubtask,
        Model = a.Model,
        Tokens = a.InputTokens is null && a.OutputTokens is null ? null : (a.InputTokens ?? 0) + (a.OutputTokens ?? 0),
        CostUsd = a.CostUsd,
        FilesChanged = a.FilesChanged,
        ToolCount = a.ToolCount,
        DurationMs = a.DurationMs,
        Summary = summaries.TryGetValue(a.AgentRunId, out var s) ? s : null,
    };

    private static DeliveryBlock? DeliveryFrom(string idPrefix, long seq, RoomTurnFacts f)
    {
        if (f.Delivery is not { } d) return null;

        return new DeliveryBlock
        {
            Id = $"{idPrefix}:delivery", Seq = seq,
            Title = d.Title, Reference = d.Reference, BranchHead = d.BranchHead, BranchBase = d.BranchBase,
            Checks = d.Checks, ChecksOk = d.ChecksOk, Url = d.Url,
        };
    }

    // ─── summary + diagnostic ───────────────────────────────────────────────────────

    /// <summary>The turn's headline (the AI's reply lead) — the model's stop summary on success, else a lead composed from the agents' result summaries (so the reply is never voiceless); the humanized cause on failure; null while in-progress / waiting (the status word conveys that).</summary>
    private static string? SummaryFor(WorkflowRunStatus status, IReadOnlyList<RunPhase> tape, IReadOnlyList<RunPhase> structural, string? error, RoomTurnFacts facts) => status switch
    {
        WorkflowRunStatus.Success => StopSummary(tape) ?? ComposeLead(tape, facts.AgentSummaries) ?? FactualLead(facts),
        WorkflowRunStatus.Failure or WorkflowRunStatus.Cancelled => FailureText(status, tape.Count > 0 ? tape : structural, error),
        _ => null,
    };

    private static string? StopSummary(IReadOnlyList<RunPhase> tape) =>
        tape.LastOrDefault(p => p.Kind == SupervisorDecisionKinds.Stop)?.Summary is { Length: > 0 } s ? s : null;

    /// <summary>The fallback lead when the supervisor wrote no stop summary — the agents' own result summaries, in spawn order, stitched into a short report. Null when no agent produced a summary (the stat rows then carry the turn).</summary>
    private static string? ComposeLead(IReadOnlyList<RunPhase> tape, IReadOnlyDictionary<Guid, string> summaries)
    {
        if (summaries.Count == 0) return null;

        var lines = tape.Where(p => SupervisorDecisionKinds.StagesAgents(p.Kind)).SelectMany(p => p.Agents)
            .Select(a => a.AgentRunId).Distinct()
            .Select(id => summaries.TryGetValue(id, out var s) ? s.TrimEnd('.', ' ') : null)
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .ToList();

        return lines.Count == 0 ? null : string.Join(". ", lines) + ".";
    }

    /// <summary>The last-resort lead — a short factual recap from the turn facts (the sanctioned "template floor") when the model authored no summary, so a successful reply is never voiceless. Null when there's nothing to state.</summary>
    private static string? FactualLead(RoomTurnFacts f)
    {
        var subtasks = f.Subtasks.Count;
        var files = f.ChangedFiles.Count;

        if (subtasks > 0 && files > 0) return $"Worked through {Count(subtasks, "subtask")} and changed {Count(files, "file")}.";
        if (files > 0) return $"Changed {Count(files, "file")}.";
        if (subtasks > 0) return $"Worked through {Count(subtasks, "subtask")}.";

        return null;
    }

    /// <summary>The rich failure diagnostic — a humanized cause, an optional headline + typed remediation (a rejected credential → Fix credentials), and the raw engine error behind "Show raw error".</summary>
    private static DiagnosticBlock RichDiagnostic(string idPrefix, long seq, WorkflowRunStatus status, string? error, RoomTurnFacts facts, IReadOnlyList<RunPhase> narrativePhases)
    {
        var raw = facts.RawError ?? error;
        var auth = IsAuthError(raw);

        return new DiagnosticBlock
        {
            Id = $"{idPrefix}:diagnostic",
            Seq = seq,
            Tone = NarrativeTone.Error,
            Title = auth ? "Authentication failed" : null,
            Text = auth
                ? "CodeSpace couldn't reach the model provider — the credential was rejected. Update it and re-run this turn."
                : FailureText(status, narrativePhases, error),
            Actions = auth
                ? new[] { new RoomAction { Kind = RoomActionKind.FixCredentials, Label = "Fix credentials", Enabled = true } }
                : Array.Empty<RoomAction>(),
            RawDetail = raw is { Length: > 0 } r && r != FailureText(status, narrativePhases, error) ? r : null,
        };
    }

    /// <summary>A rejected model credential — the one error class with a typed remediation (Fix credentials) rather than just a rerun.</summary>
    private static bool IsAuthError(string? error)
    {
        if (string.IsNullOrWhiteSpace(error)) return false;

        return new[] { "401", "unauthorized", "authentication", "api key", "api-key", "credential", "invalid_api_key" }
            .Any(m => error.Contains(m, StringComparison.OrdinalIgnoreCase));
    }

    private static string FailureText(WorkflowRunStatus status, IReadOnlyList<RunPhase> narrativePhases, string? error)
    {
        if (status == WorkflowRunStatus.Cancelled) return "This turn was cancelled.";

        var phaseDetail = narrativePhases.LastOrDefault(p => p.Status == PhaseStatus.Failed && !string.IsNullOrWhiteSpace(p.Summary))?.Summary;

        return phaseDetail ?? Humanize(error);
    }

    private static string Humanize(string? error)
    {
        if (string.IsNullOrWhiteSpace(error)) return "This turn ended with an error.";

        const string marker = "failed: ";
        var idx = error.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        var msg = (idx >= 0 ? error[(idx + marker.Length)..] : error).Trim();

        return msg.Length == 0 || msg.StartsWith("Node '", StringComparison.OrdinalIgnoreCase) ? "This turn ended with an error." : msg;
    }

    // ─── helpers ────────────────────────────────────────────────────────────────────

    private static string Count(int n, string noun) => $"{n} {noun}{(n == 1 ? "" : "s")}";

    private static string AgentWord(int count) => count == 1 ? "1 agent" : $"{count} agents";

    /// <summary>Whether an agent's (string) status is terminal — via the shared <see cref="AgentRunStateMachine"/>, so the Work stage can't drift from the lifecycle (NeedsReview is terminal).</summary>
    private static bool IsAgentTerminal(string status) => Enum.TryParse<AgentRunStatus>(status, out var s) && AgentRunStateMachine.IsTerminal(s);

    private static string? PhaseDuration(RunPhase p)
    {
        if (p.StartedAt is not { } start) return null;

        var ms = (long)((p.CompletedAt ?? start) - start).TotalMilliseconds;
        return ms > 0 ? FormatDuration(ms) : null;
    }

    private static string FormatDuration(long ms)
    {
        var s = (int)(ms / 1000);
        return s < 60 ? $"{s}s" : $"{s / 60}m {s % 60}s";
    }

    private static ExecutionStepStatus MapStatus(PhaseStatus status) => status switch
    {
        PhaseStatus.Active => ExecutionStepStatus.Running,
        PhaseStatus.Waiting => ExecutionStepStatus.Blocked,
        PhaseStatus.Succeeded => ExecutionStepStatus.Done,
        PhaseStatus.Failed => ExecutionStepStatus.Failed,
        _ => ExecutionStepStatus.Pending,
    };
}
