using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Tasks.Phases.Sources.Nodes;
using CodeSpace.Core.Services.Tasks.Phases.Sources.Supervisor;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Agents.Benchmark;
using CodeSpace.Messages.Dtos.Sessions.Room;
using CodeSpace.Messages.Enums;
using CodeSpace.Messages.Plans;
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
        var authored = phases.Where(p => p.SourceKey == SupervisorPhaseSource.Key && p.Kind == SupervisorPhaseSource.AuthoredPhaseKind).OrderBy(p => p.Order).ToList();
        var structural = phases.Where(p => p.SourceKey == WorkflowNodePhaseSource.Key).OrderBy(p => p.Order).ToList();

        // A supervisor turn draws a DYNAMIC map from its real phases (Start → Plan → each authored phase → Deliver);
        // a non-supervisor run keeps its structural node spine.
        var map = tape.Count > 0
            ? BuildDynamicMap(idPrefix, seq, tape, authored, status, facts.AcceptancePassed, facts.FinalAnswer?.Degraded ?? false)
            : BuildMap(idPrefix, seq, structural);

        var blocks = BuildBlocks(idPrefix, seq, status, error, decisions, facts, tape.Count > 0 ? tape : structural, authored);

        return new TurnNarrative(SummaryFor(status, tape, structural, error, facts), map, blocks);
    }

    // ─── execution map ───────────────────────────────────────────────────────────

    /// <summary>
    /// The DYNAMIC supervisor map — derived from the run's real phases, so it GROWS with the work instead of a fixed
    /// template: Start → Plan → one step PER authored phase (the model's own "调研与分析" / "综合与建议" grouping, or a
    /// single "Work" step for a flat plan) → Review → Deliver. Per-step detail is the plan duration, each phase's live
    /// agent progress ("k of N" / "N agents"), the acceptance verdict, and the deliver outcome. Labels are the real
    /// phase titles, not per-run copy — the map GROWS as the supervisor re-plans / adds phases.
    /// </summary>
    private static ExecutionMapBlock BuildDynamicMap(string idPrefix, long seq, IReadOnlyList<RunPhase> tape, IReadOnlyList<RunPhase> authored, WorkflowRunStatus status, bool? acceptancePassed, bool degraded)
    {
        var plan = tape.FirstOrDefault(p => p.Kind == SupervisorDecisionKinds.Plan);
        var agents = tape.Where(p => SupervisorDecisionKinds.StagesAgents(p.Kind)).SelectMany(p => p.Agents).ToList();

        var active = status is WorkflowRunStatus.Pending or WorkflowRunStatus.Enqueued or WorkflowRunStatus.Running or WorkflowRunStatus.Suspended;
        var failed = status is WorkflowRunStatus.Failure or WorkflowRunStatus.Cancelled;
        var succeeded = status == WorkflowRunStatus.Success;

        var steps = new List<ExecutionMapStep> { Step($"{idPrefix}:start", "Start", ExecutionStepStatus.Done, null) };

        steps.Add(Step($"{idPrefix}:plan", "Plan", plan != null ? ExecutionStepStatus.Done : active ? ExecutionStepStatus.Running : ExecutionStepStatus.Pending, plan != null ? PhaseDuration(plan) : null));

        if (authored.Count > 0)
            steps.AddRange(authored.Select((p, i) => { var (s, d) = WorkStage(true, p.Agents, active); return Step($"{idPrefix}:ph{i}", p.Label, MapStatus(p.Status) is var ms && ms != ExecutionStepStatus.Pending ? ms : s, d); }));
        else
        {
            var (ws, wd) = WorkStage(plan != null, agents, active);
            steps.Add(Step($"{idPrefix}:work", "Work", ws, wd));
        }

        var (workStatus, _) = WorkStage(plan != null, agents, active);
        var (reviewStatus, reviewDetail) = ReviewStage(succeeded, failed, workStatus, acceptancePassed, degraded);
        steps.Add(Step($"{idPrefix}:review", "Review", reviewStatus, reviewDetail));

        var (deliverStatus, deliverDetail) = DeliverStage(succeeded, failed, degraded);
        steps.Add(Step($"{idPrefix}:deliver", "Deliver", deliverStatus, deliverDetail));

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

    /// <summary>Review folds from the objective acceptance verdict when graded, else from the run outcome: a DEGRADED stop (a fail-closed give-up) → stopped (never a green "passed" for a run that gave up); success → passed; failed-at-work → skipped; failed-at-review → failed; else queued / running once work is done.</summary>
    private static (ExecutionStepStatus, string?) ReviewStage(bool succeeded, bool failed, ExecutionStepStatus work, bool? acceptancePassed, bool degraded)
    {
        if (degraded) return (ExecutionStepStatus.Skipped, "stopped");

        if (acceptancePassed is true) return (ExecutionStepStatus.Done, "passed");
        if (acceptancePassed is false) return (ExecutionStepStatus.Failed, "failed");

        if (succeeded) return (ExecutionStepStatus.Done, "passed");
        if (failed) return work == ExecutionStepStatus.Failed ? (ExecutionStepStatus.Skipped, "skipped") : (ExecutionStepStatus.Failed, "failed");
        return work == ExecutionStepStatus.Done ? (ExecutionStepStatus.Running, null) : (ExecutionStepStatus.Queued, "queued");
    }

    /// <summary>Deliver folds from the run outcome: a DEGRADED stop delivered nothing → skipped; success → Done (the PR reference rides the delivery card); failed → skipped; else queued.</summary>
    private static (ExecutionStepStatus, string?) DeliverStage(bool succeeded, bool failed, bool degraded)
    {
        if (degraded) return (ExecutionStepStatus.Skipped, "stopped");
        if (succeeded) return (ExecutionStepStatus.Done, null);
        if (failed) return (ExecutionStepStatus.Skipped, "skipped");
        return (ExecutionStepStatus.Queued, "queued");
    }

    // ─── inner blocks ──────────────────────────────────────────────────────────────

    /// <summary>
    /// The turn's inner blocks as a REAL, SEQUENTIAL per-round narrative (never lumped): for each round —
    /// "Plan · N subtasks" (that round's subtasks) → that round's Agent cards → the supervisor's operation — then the
    /// turn-level aggregates (files / tools), the delivery, the pending decisions, the failure diagnostic, the rich final
    /// answer, and — while active — the live "working…" indicator pinned last. Rounds come from the model's authored
    /// phases (the natural grouping) when present, else the plan-segmented tape rounds, else a single fan-out group.
    /// The frontend renders this list strictly in order — ordering is emission order, so there's no FE ordering logic.
    /// </summary>
    private static IReadOnlyList<RoomBlock> BuildBlocks(string idPrefix, long seq, WorkflowRunStatus status, string? error, IReadOnlyList<DecisionBlock> decisions, RoomTurnFacts facts, IReadOnlyList<RunPhase> narrativePhases, IReadOnlyList<RunPhase> authored)
    {
        var blocks = new List<RoomBlock>();
        var agentById = narrativePhases.SelectMany(p => p.Agents).GroupBy(a => a.AgentRunId).ToDictionary(g => g.Key, g => g.First());

        // The plan checklist leads the turn when the run persisted a plan — the whole current version as a live
        // tracker. It SUBSUMES the per-round "Plan · N subtasks" rows (suppressed below), so plan titles never
        // render twice; a plan-less run projects exactly as before.
        if (facts.Checklist is { } checklist)
            blocks.Add(PlanChecklist(idPrefix, seq, checklist));

        if (authored.Count > 0)
        {
            for (var i = 0; i < authored.Count; i++)
                EmitAuthoredRound(blocks, idPrefix, seq, authored[i], i + 1, facts);

            EmitRetrySteps(blocks, idPrefix, seq, facts, agentById);
            EmitRespawnSteps(blocks, idPrefix, seq, facts, agentById);
        }
        else if (facts.Rounds.Count > 0)
        {
            foreach (var round in facts.Rounds)
                EmitTapeRound(blocks, idPrefix, seq, status, round, facts, agentById);

            EmitRetrySteps(blocks, idPrefix, seq, facts, agentById);
        }
        else if (AgentGroup(idPrefix, seq, status, facts, narrativePhases) is { } fanout)
        {
            blocks.Add(fanout);
        }

        if (FilesStat(idPrefix, seq, facts, FileProducers(facts, agentById)) is { } files) blocks.Add(files);
        if (ToolsStat(idPrefix, seq, facts) is { } tools) blocks.Add(tools);

        if (DeliveryFrom(idPrefix, seq, facts) is { } delivery) blocks.Add(delivery);

        // Pending decisions are "now" — the current ask.
        blocks.AddRange(decisions);

        if (status is WorkflowRunStatus.Failure or WorkflowRunStatus.Cancelled)
            blocks.Add(RichDiagnostic(idPrefix, seq, status, error, facts, narrativePhases));

        // The green "RESULT" card is a SUCCESS artifact — only a succeeded run delivers an answer. A failed / cancelled
        // run's outcome is the error diagnostic above, never a green Result echoing the failure text.
        if (status == WorkflowRunStatus.Success && facts.FinalAnswer is { } fa) blocks.Add(FinalAnswerFrom(idPrefix, seq, fa, FileProducers(facts, agentById)));

        if (IsActive(status) && LiveActivity(idPrefix, seq, facts) is { } live) blocks.Add(live);

        return blocks;
    }

    // ─── rounds (Plan · N subtasks → Agents → operation, in real execution order) ────

    /// <summary>One AUTHORED-phase round — the model's own subtask grouping (调研与分析 / 综合与建议): "Plan · N subtasks" (from the phase's agents' assigned subtasks) then that phase's Agent cards, titled by the phase.</summary>
    private static void EmitAuthoredRound(List<RoomBlock> blocks, string idPrefix, long seq, RunPhase phase, int index, RoomTurnFacts facts)
    {
        var subtasks = phase.Agents.Select(a => a.AssignedSubtask ?? a.Goal).Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s!).Distinct().ToList();

        if (subtasks.Count > 0 && facts.Checklist == null)
            blocks.Add(new StatBlock { Id = $"{idPrefix}:r{index}:plan", Seq = seq, Kind = "subtasks", Label = "Plan", Detail = Count(subtasks.Count, "subtask"), Items = subtasks.Select(t => new StatItem { Text = t }).ToList() });

        if (phase.Agents.Count > 0)
            blocks.Add(new AgentGroupBlock { Id = $"{idPrefix}:r{index}:agents", Seq = seq, Title = phase.Label, Agents = phase.Agents.Select(a => ToCard(a, facts)).ToList() });
    }

    /// <summary>One TAPE round (a flat plan / a re-plan) — "Plan · N subtasks" (the round's plan) then its spawned Agent cards then the round's closing supervisor operation.</summary>
    private static void EmitTapeRound(List<RoomBlock> blocks, string idPrefix, long seq, WorkflowRunStatus status, RoomRound r, RoomTurnFacts facts, IReadOnlyDictionary<Guid, PhaseAgentRef> agentById)
    {
        if (r.Subtasks.Count > 0 && facts.Checklist == null)
            blocks.Add(new StatBlock { Id = $"{idPrefix}:r{r.Index}:plan", Seq = seq, Kind = "subtasks", Label = "Plan", Detail = Count(r.Subtasks.Count, "subtask"), Items = r.Subtasks.Select(t => new StatItem { Text = t }).ToList() });

        // Exclude the retry agents — they render CHRONOLOGICALLY as their own cards after each retry step (EmitRetrySteps),
        // so this round group is the INITIAL spawn only. Without this, a retried subtask's fresh agent would render twice
        // (here in the round bag AND in its retry card).
        var retryIds = RetryAgentIds(facts);
        var agents = r.AgentRunIds.Distinct().Where(id => !retryIds.Contains(id)).Select(id => agentById.GetValueOrDefault(id)).Where(a => a != null).Select(a => a!).ToList();

        if (agents.Count > 0)
            blocks.Add(new AgentGroupBlock { Id = $"{idPrefix}:r{r.Index}:agents", Seq = seq, Title = IsActive(status) ? "Work" : "Agents", Agents = agents.Select(a => ToCard(a, facts)).ToList() });
    }

    /// <summary>
    /// The supervisor's retry beats, in tape order — each a "Supervisor retried a subtask" step followed by that retry's
    /// OWN single-agent card (the fresh agent, self-labeled with the subtask it re-ran via <c>AssignedSubtask</c>), so a
    /// failed-then-retried subtask reads as the chronological recovery it was, not a lump in the initial group. The card
    /// is looked up in <paramref name="agentById"/> (the retry agent rides the per-decision Retry tape phase); a no-op
    /// retry (nothing staged) is just the line. No-op when the turn had no retries.
    /// </summary>
    private static void EmitRetrySteps(List<RoomBlock> blocks, string idPrefix, long seq, RoomTurnFacts facts, IReadOnlyDictionary<Guid, PhaseAgentRef> agentById)
    {
        foreach (var step in facts.RetrySteps)
            if (step.AgentRunId is { } id && agentById.TryGetValue(id, out var agent))
                blocks.Add(new AgentGroupBlock { Id = $"{idPrefix}:retry:{step.Sequence}:agent", Seq = seq, Title = "Retry", Agents = new[] { ToCard(agent, facts) } });
    }

    /// <summary>
    /// The supervisor's re-spawn waves, in tape order — each a "Supervisor spawned N agents again" step followed by that
    /// wave's OWN agent cards (the fresh agents the second/third spawn staged, one of which may have failed). The authored
    /// phase group anchors only each subtask's FIRST attempt, so without this a later wave (and its failed agent) vanishes
    /// from the room though Activity shows it. The cards are looked up in <paramref name="agentById"/> (the re-spawn agents
    /// ride the per-decision Spawn tape phase); a wave whose agents didn't resolve is skipped. No-op when the turn had none.
    /// </summary>
    private static void EmitRespawnSteps(List<RoomBlock> blocks, string idPrefix, long seq, RoomTurnFacts facts, IReadOnlyDictionary<Guid, PhaseAgentRef> agentById)
    {
        foreach (var wave in facts.RespawnSteps)
        {
            var agents = wave.AgentRunIds.Select(id => agentById.GetValueOrDefault(id)).Where(a => a != null).Select(a => a!).ToList();

            if (agents.Count == 0) continue;

            blocks.Add(new AgentGroupBlock { Id = $"{idPrefix}:respawn:{wave.Sequence}:agents", Seq = seq, Title = "Work", Agents = agents.Select(a => ToCard(a, facts)).ToList() });
        }
    }

    /// <summary>The set of retry-staged agent ids — kept out of the round/phase groups so a retry's fresh agent renders ONCE, as its own chronological card.</summary>
    private static HashSet<Guid> RetryAgentIds(RoomTurnFacts facts) =>
        facts.RetrySteps.Where(s => s.AgentRunId is not null).Select(s => s.AgentRunId!.Value).ToHashSet();

    /// <summary>The rich final answer — the closing text + typed attachments (files / PR / images), each rendered distinctly.</summary>
    private static FinalAnswerBlock FinalAnswerFrom(string idPrefix, long seq, RoomFinalAnswer fa, IReadOnlyDictionary<string, (Guid AgentRunId, string Label)> producers) => new()
    {
        Id = $"{idPrefix}:final", Seq = seq,
        Text = fa.Text,
        Attachments = fa.Attachments.Select(a => Attach(a, producers)).ToList(),
        Degraded = fa.Degraded,
    };

    /// <summary>Map a fact attachment to the DTO, attributing a FILE to its producing agent (so the RESULT never presents an intermediate agent's file as the final deliverable without saying whose it is).</summary>
    private static AnswerAttachment Attach(RoomAttachment a, IReadOnlyDictionary<string, (Guid AgentRunId, string Label)> producers)
    {
        var producer = a.Kind == AnswerAttachmentKind.FileLink && producers.TryGetValue(a.Label, out var p) ? p : ((Guid, string)?)null;

        return new AnswerAttachment
        {
            Kind = a.Kind, Label = a.Label, Url = a.Url, PreviewUrl = a.PreviewUrl, DownloadUrl = a.DownloadUrl,
            AgentRunId = producer?.Item1,
            Producer = producer?.Item2,
        };
    }

    /// <summary>Reverse the per-agent file map into path → (producing agent run id, its card label) so the final answer can attribute each file. Last writer wins on a shared path (matches the newest-writer preview resolution).</summary>
    private static IReadOnlyDictionary<string, (Guid, string)> FileProducers(RoomTurnFacts facts, IReadOnlyDictionary<Guid, PhaseAgentRef> agentById)
    {
        var map = new Dictionary<string, (Guid, string)>(StringComparer.Ordinal);

        foreach (var (agentRunId, files) in facts.AgentFiles)
            foreach (var path in files)
                map[path] = (agentRunId, agentById.TryGetValue(agentRunId, out var a) ? CardLabel(a) : "an agent");

        return map;
    }

    /// <summary>The agent's display label — the same derivation the card uses (role → subtask → goal → label).</summary>
    private static string CardLabel(PhaseAgentRef a) => a.Role ?? a.AssignedSubtask ?? a.Goal ?? a.Label ?? "Agent";

    /// <summary>The live "working…" indicator pinned last on an active turn — the running agents' latest public activity, else the reasoning tail, else a generic "Working…".</summary>
    private static LiveActivityBlock LiveActivity(string idPrefix, long seq, RoomTurnFacts facts)
    {
        var line = facts.LatestLines.Values.FirstOrDefault(s => !string.IsNullOrWhiteSpace(s))
            ?? (facts.ReasoningSteps.Count > 0 ? facts.ReasoningSteps[^1] : null);

        return new LiveActivityBlock { Id = $"{idPrefix}:live", Seq = seq, Text = string.IsNullOrWhiteSpace(line) ? "Working…" : line!, AgentRunId = facts.LatestLines.Count > 0 ? facts.LatestLines.Keys.First() : null };
    }

    private static bool IsActive(WorkflowRunStatus status) => status is WorkflowRunStatus.Pending or WorkflowRunStatus.Enqueued or WorkflowRunStatus.Running or WorkflowRunStatus.Suspended;

    private static bool IsTerminal(WorkflowRunStatus status) => status is WorkflowRunStatus.Success or WorkflowRunStatus.Failure or WorkflowRunStatus.Cancelled;

    // ─── the plan checklist (the whole current plan as a live tracker) ──────────────

    /// <summary>Map the derived checklist read model onto the render block — every string authored HERE (states pass through as the open vocabulary; dependency ids become 1-based ordinals; acceptance specs become chip labels).</summary>
    private static PlanChecklistBlock PlanChecklist(string idPrefix, long seq, WorkPlanChecklist checklist)
    {
        // First-wins on a duplicate item id: the supervisor's plan validator TOLERATES dup subtask ids (a
        // degenerate flat plan), so this mapper must render both lines rather than throw and kill the room.
        var ordinalById = checklist.Items.Select((it, i) => (it.Item.Id, Ordinal: i + 1))
            .GroupBy(x => x.Id, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First().Ordinal, StringComparer.Ordinal);

        return new PlanChecklistBlock
        {
            Id = $"{idPrefix}:plan-checklist",
            Seq = seq,
            Label = "Plan",
            Version = checklist.Version,
            Status = checklist.Status,
            Detail = ChecklistDetail(checklist.Items),
            Items = checklist.Items.Select((it, i) => ChecklistItem(it, i + 1, ordinalById)).ToList(),
            Assumptions = checklist.Assumptions ?? Array.Empty<string>(),
            Questions = (checklist.Questions ?? Array.Empty<WorkPlanQuestion>()).Select(RoomQuestion).ToList(),
            HasPriorVersions = checklist.Version > 1,
        };
    }

    /// <summary>"5 items · 2 done · 1 running · 1 failed · 1 needs review" — the item count plus every non-zero non-pending state, in severity order. Just "N items" when nothing has started.</summary>
    private static string ChecklistDetail(IReadOnlyList<WorkPlanChecklistItem> items)
    {
        var parts = new List<string> { Count(items.Count, "item") };

        Append(WorkPlanItemStates.Completed, _ => "done");
        Append(WorkPlanItemStates.InProgress, _ => "running");
        Append(WorkPlanItemStates.Failed, _ => "failed");
        Append(WorkPlanItemStates.NeedsReview, n => n == 1 ? "needs review" : "need review");

        return string.Join(" · ", parts);

        void Append(string state, Func<int, string> word)
        {
            var n = items.Count(i => i.State == state);
            if (n > 0) parts.Add($"{n} {word(n)}");
        }
    }

    private static PlanChecklistItem ChecklistItem(WorkPlanChecklistItem it, int ordinal, IReadOnlyDictionary<string, int> ordinalById) => new()
    {
        Ordinal = ordinal,
        ItemId = it.Item.Id,
        Title = it.Item.Title,
        Kind = it.Item.Kind,
        State = it.State,
        DependsOn = (it.Item.DependsOn ?? Array.Empty<string>()).Select(id => ordinalById.TryGetValue(id, out var o) ? o : 0).Where(o => o > 0).ToList(),
        AcceptanceLabel = it.Item.Acceptance is { } spec ? string.Join(" ", spec.Command ?? Array.Empty<string>()) : null,
        AcceptanceKind = it.Item.Acceptance is { } s ? (s.Kind ?? BenchmarkGradingKind.TestsPass).ToString() : null,
        AcceptancePassed = it.AcceptancePassed,
        AcceptanceDetail = it.AcceptanceDetail,
        AcceptanceCriteria = it.Item.AcceptanceCriteria ?? Array.Empty<string>(),
        AgentRunId = it.AgentRunId,
        Attempts = it.Attempts,
    };

    private static RoomPlanQuestion RoomQuestion(WorkPlanQuestion q) => new()
    {
        Id = q.Id,
        Question = q.Question,
        Options = (q.Options ?? Array.Empty<WorkPlanQuestionOption>()).Select(o => new RoomPlanQuestionOption { Id = o.Id, Label = o.Label, Recommended = o.Id == q.RecommendedOptionId }).ToList(),
        AllowFreeText = q.AllowFreeText,
    };

    // ─── stat rows (label · detail, design vocabulary) ──────────────────────────────

    /// <summary>The "Files changed" row. Each path is attributed to its producing agent ("from {label}") via the SAME newest-accepted-writer-wins map the RESULT card uses — so when several agents (across waves) wrote one path, the row names whose version is the FINAL one. An un-attributed path (no per-agent entry — e.g. a supervisor-direct edit, or beyond the per-agent file cap) carries no attribution.</summary>
    private static StatBlock? FilesStat(string idPrefix, long seq, RoomTurnFacts f, IReadOnlyDictionary<string, (Guid AgentRunId, string Label)> producers) =>
        f.ChangedFiles.Count == 0 ? null : new StatBlock { Id = $"{idPrefix}:stat:files", Seq = seq, Kind = "files", Label = "Files changed", Detail = FilesDetail(f.Additions, f.Deletions, f.ChangedFiles.Count), Items = f.ChangedFiles.Select(p => new StatItem { Text = p, Detail = producers.TryGetValue(p, out var pr) ? $"from {pr.Label}" : null }).ToList() };

    /// <summary>The tools row — collapsed to just the total ("129 calls"); expanding reveals the per-tool breakdown (Read · 40, WebSearch · 15, …), one item per real tool NAME. A summary, not the raw per-call stream.</summary>
    private static StatBlock? ToolsStat(string idPrefix, long seq, RoomTurnFacts f) =>
        f.ToolCalls is not > 0 ? null : new StatBlock { Id = $"{idPrefix}:stat:tools", Seq = seq, Kind = "tools", Label = "Tools", Detail = Count(f.ToolCalls.Value, "call"), Items = f.ToolHistogram.Select(h => new StatItem { Text = h.Kind, Detail = h.Count.ToString() }).ToList() };

    /// <summary>"+148 −32 · 6 files" when the diff stat is captured, else just "6 files" (the line counts are a true gap → no "+X −Y").</summary>
    private static string FilesDetail(int? additions, int? deletions, int count)
    {
        var files = Count(count, "file");
        return DiffStat(additions, deletions) is { } diff ? $"{diff} · {files}" : files;
    }

    private static string? DiffStat(int? additions, int? deletions) =>
        additions is { } a && deletions is { } d ? $"+{a} −{d}" : null;   // "+148 −32" (U+2212 minus); null until the diff stat is captured

    // ─── agents ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The turn's agents as one group — the design's "Agents · N ran in parallel" (collapsed) / "Work · N agents"
    /// (live) section. Folds EVERY phase that carries agents (a supervisor spawn AND a flow.map fan-out), with the
    /// already-projected <see cref="PhaseAgentRef"/> meta (zero extra read) + the per-agent result summary the projector
    /// gathered. Renders for ANY agent — a single-agent turn still gets its ONE card so its terminal + output are
    /// reachable (not just the execution dots). Null only when no phase carried an agent.
    /// </summary>
    private static AgentGroupBlock? AgentGroup(string idPrefix, long seq, WorkflowRunStatus status, RoomTurnFacts facts, IReadOnlyList<RunPhase> phases)
    {
        var agents = phases.SelectMany(p => p.Agents)
            .GroupBy(a => a.AgentRunId).Select(g => g.First()).ToList();

        if (agents.Count == 0) return null;

        var active = status is WorkflowRunStatus.Pending or WorkflowRunStatus.Enqueued or WorkflowRunStatus.Running or WorkflowRunStatus.Suspended;

        return new AgentGroupBlock
        {
            Id = $"{idPrefix}:agents", Seq = seq,
            Title = active ? "Work" : "Agents",
            Agents = agents.Select(a => ToCard(a, facts)).ToList(),
        };
    }

    private static RoomAgentCard ToCard(PhaseAgentRef a, RoomTurnFacts facts) => new()
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
        ChangedFiles = facts.AgentFiles.TryGetValue(a.AgentRunId, out var files) ? files : Array.Empty<string>(),
        ToolCount = a.ToolCount,
        DurationMs = a.DurationMs,
        Summary = facts.AgentSummaries.TryGetValue(a.AgentRunId, out var s) ? s : null,
        LatestLine = facts.LatestLines.TryGetValue(a.AgentRunId, out var l) ? l : null,
        NodeId = a.NodeId,
        IterationKey = a.IterationKey,
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
        WorkflowRunStatus.Success => StopSummary(tape) ?? ComposeLead(tape, facts.AgentSummaries) ?? SoleAgentSummary(facts.AgentSummaries) ?? FactualLead(facts),
        WorkflowRunStatus.Failure or WorkflowRunStatus.Cancelled => FailureText(status, tape.Count > 0 ? tape : structural, facts.RawError ?? error),
        _ => null,
    };

    /// <summary>A single-agent (non-supervisor) turn's lead — that one agent's own summary. The supervisor tape is empty, so <see cref="ComposeLead"/> sees nothing; this is the plain agent turn's voice. Null unless exactly one agent produced a summary.</summary>
    private static string? SoleAgentSummary(IReadOnlyDictionary<Guid, string> summaries) =>
        summaries.Count == 1 && summaries.Values.Single() is { Length: > 0 } s ? s : null;

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

        // Humanize the DEEP error (raw), not the generic run-row error — so the headline is the specific cause
        // ("OpenAI API error … the request timed out") the projector surfaced, not the "Node 'sup' failed." placeholder.
        // The auth case keeps its typed remediation copy; either way `text` is the DISPLAYED line the raw dedup compares to.
        var text = auth
            ? "CodeSpace couldn't reach the model provider — the credential was rejected. Update it and re-run this turn."
            : FailureText(status, narrativePhases, raw);

        return new DiagnosticBlock
        {
            Id = $"{idPrefix}:diagnostic",
            Seq = seq,
            Tone = NarrativeTone.Error,
            Title = auth ? "Authentication failed" : null,
            Text = text,
            Actions = auth
                ? new[] { new RoomAction { Kind = RoomActionKind.FixCredentials, Label = "Fix credentials", Enabled = true } }
                : Array.Empty<RoomAction>(),
            RawDetail = raw is { Length: > 0 } r && r != text ? r : null,
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
        return ms >= 1000 ? FormatDuration(ms) : null;   // sub-second is noise ("0s") — omit the detail
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
