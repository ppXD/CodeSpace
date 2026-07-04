using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Agents.Cost;
using CodeSpace.Core.Services.Supervisor;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Decisions;
using CodeSpace.Messages.Enums;
using CodeSpace.Messages.Tasks.Phases;
using Microsoft.EntityFrameworkCore;

namespace CodeSpace.Core.Services.Tasks.Phases.Sources.Supervisor;

/// <summary>
/// The SUPERVISOR-LEDGER phase source — it reuses <see cref="ISupervisorDecisionLog.GetForRunAsync"/> (team-scoped,
/// ordered by Sequence) and projects ONE <see cref="RunPhase"/> per decision. For spawn / retry decisions the Agents
/// are the REAL <c>AgentRun</c> rows the outcome staged (read team-scoped by id, folding the GROUND-TRUTH status —
/// mirroring <c>SupervisorScorecardService</c>, never the decider's self-report). For ask_human the Summary is the
/// question (+ answer if present) and the phase is Waiting until answered. Ordered AFTER the structural node phases
/// via a high base offset (<see cref="OrderBase"/> + Sequence), so the board shows the graph spine first, then the
/// supervisor's decision tape. A non-supervisor run (empty ledger) contributes zero phases. READ-ONLY.
/// </summary>
public sealed class SupervisorPhaseSource : IRunPhaseSource, IScopedDependency
{
    public const string Key = "supervisor-ledger";

    /// <summary>High base offset so ledger phases sort AFTER the structural node phases (which use small monotonic indices). Each decision then orders by its per-run Sequence.</summary>
    public const int OrderBase = 1_000_000;

    private readonly ISupervisorDecisionLog _ledger;
    private readonly CodeSpaceDbContext _db;

    public SupervisorPhaseSource(ISupervisorDecisionLog ledger, CodeSpaceDbContext db)
    {
        _ledger = ledger;
        _db = db;
    }

    public string SourceKey => Key;

    public async Task<IReadOnlyList<RunPhase>> ContributeAsync(RunPhaseContext context, CancellationToken cancellationToken)
    {
        var decisions = await _ledger.GetForRunAsync(context.RunId, context.TeamId, cancellationToken).ConfigureAwait(false);

        if (decisions.Count == 0) return Array.Empty<RunPhase>();

        var ids = decisions.SelectMany(StagedAgentIds).Distinct().ToList();

        var runs = await SpawnedAgentRowsAsync(context.TeamId, ids, cancellationToken).ConfigureAwait(false);
        var toolCounts = await AgentMetricsReader.ToolCountsByAgentAsync(_db, context.TeamId, ids, cancellationToken).ConfigureAwait(false);

        var agentStatusById = runs.ToDictionary(r => r.Id, r => r.Status);
        var extrasByAgent = ExtrasById(runs, toolCounts, DateTimeOffset.UtcNow);

        return ProjectDecisions(decisions, agentStatusById, extrasByAgent);
    }

    /// <summary>High base offset for the model-authored SEMANTIC PHASES (L4 arc C) — they sort AFTER both the structural node phases AND the per-decision tape, as their own top-level band on the board.</summary>
    public const int PhaseOrderBase = 2_000_000;

    /// <summary>The <see cref="RunPhase.Kind"/> of a model-authored semantic phase (L4 arc C) — the shared discriminator the room projector reads to separate "the plan's shape" (the map) from "the decision tape" (the narrative).</summary>
    public const string AuthoredPhaseKind = "phase";

    /// <summary>Bound on the per-agent changed-file list carried on the ref for the terminal's Files tab (the count stays the full total).</summary>
    private const int MaxAgentFiles = 40;

    /// <summary>The pure projection step — decisions + the already-resolved ground-truth agent statuses (and the optional live duration/tool-count extras) → phases. Separated from the DB read so it is unit-testable without a DbContext. One phase per decision, PLUS the model-authored semantic phases (L4 arc C) when the plan grouped its subtasks; a flat plan adds none (the per-decision board verbatim). The compact (model/tokens) folds from the ledger; <paramref name="extrasByAgent"/> carries the figures that don't (duration, tool count) — omitted leaves those ref fields null.</summary>
    public static IReadOnlyList<RunPhase> ProjectDecisions(IReadOnlyList<SupervisorDecisionRecord> decisions, IReadOnlyDictionary<Guid, AgentRunStatus> agentStatusById, IReadOnlyDictionary<Guid, AgentRunExtras>? extrasByAgent = null)
    {
        var rollup = new AgentRollup(agentStatusById, SupervisorAgentAllocation.ResultsById(decisions), extrasByAgent ?? EmptyExtras, SupervisorAgentAllocation.Map(decisions));

        var phases = decisions.Select(d => ToPhase(d, rollup)).ToList();

        phases.AddRange(AuthoredPhases(decisions, rollup));

        return phases;
    }

    /// <summary>
    /// L4 arc C: the model-authored semantic phases off the <c>plan</c> decision, projected as their OWN
    /// <see cref="RunPhase"/>s so the board reads "Investigate / Implement / Review" rather than a flat decision tape.
    /// Additive — a flat plan (no phases) contributes none. Each phase's children are the agents the spawns staged for
    /// its grouped subtasks (a phase's <c>subtaskIds</c> mapped through the spawn payload's <c>subtaskIds[i]</c> ↔ outcome
    /// <c>agentRunIds[i]</c> staging order), with the ground-truth status; its status folds from those children.
    /// </summary>
    private static IReadOnlyList<RunPhase> AuthoredPhases(IReadOnlyList<SupervisorDecisionRecord> decisions, AgentRollup rollup)
    {
        // The LATEST plan — matching the executor, which resolves a spawn's subtasks from the most recent plan
        // (ResolvePlannedSubtasks). On a re-plan, the phases must track the same plan the spawns were built from.
        var plan = decisions.LastOrDefault(d => d.DecisionKind == SupervisorDecisionKinds.Plan);
        var authored = SupervisorOutcome.ReadPlanPhases(plan?.OutcomeJson);

        if (authored.Count == 0) return Array.Empty<RunPhase>();

        var subtaskAttempts = SubtaskAgentAttempts(decisions, plan!.Sequence);

        return authored.Select((phase, index) => ToAuthoredPhase(phase, index, plan, subtaskAttempts, rollup)).ToList();
    }

    private static RunPhase ToAuthoredPhase(SupervisorPlanPhase phase, int index, SupervisorDecisionRecord plan, IReadOnlyDictionary<string, IReadOnlyList<Guid>> subtaskAttempts, AgentRollup rollup)
    {
        // The authored group shows only the INITIAL spawn — the FIRST attempt per subtask (which IS the failed original
        // for a retried subtask, so failures stay visible and the phase still folds to Failed). The retries render
        // CHRONOLOGICALLY as their own cards after each "Supervisor retried a subtask" step, so this group reads
        // "N agents" = the plan's subtask count (not a lump of every attempt).
        var agents = phase.SubtaskIds
            .Select(id => (subtaskAttempts.GetValueOrDefault(id) ?? Array.Empty<Guid>()).FirstOrDefault())
            .Where(id => id != Guid.Empty && rollup.Knows(id))
            .Select(rollup.RefFor)
            .ToList();

        return new RunPhase
        {
            Id = $"phase-{phase.Id}",
            Label = phase.Title,
            Kind = AuthoredPhaseKind,
            Status = PhaseStatusFromAgents(agents),
            Order = PhaseOrderBase + index,
            Agents = agents,
            Metrics = PhaseAgentMetrics.From(agents),
            Summary = PhaseAcceptanceSummary(phase.Acceptance),
            SourceKey = Key,
            StartedAt = plan.CreatedDate,
            CompletedAt = null,
        };
    }

    /// <summary>
    /// subtaskId → the agent-run ids that ran it, in ATTEMPT order (the spawn's original first, then each retry's fresh
    /// agent), walking <c>spawn</c> AND <c>retry</c> decisions in Sequence order (a spawn fans the payload
    /// <c>subtaskIds[i]</c> ↔ outcome <c>agentRunIds[i]</c>; a retry re-runs ONE subtask as a fresh agent). A retried
    /// subtask therefore carries BOTH its failed original AND the retry, so the room can render the full trajectory
    /// (the failure + its recovery); a never-retried subtask yields a single-element list.
    ///
    /// <para>Scoped to decisions AFTER the LATEST plan (<paramref name="planSequence"/>) — subtask ids are model-authored
    /// and plan-local (not unique across a re-plan), and the phases render the latest plan only, so an earlier plan that
    /// reused an id must not leak its superseded (possibly failed) agent into this plan's phase.</para>
    /// </summary>
    private static IReadOnlyDictionary<string, IReadOnlyList<Guid>> SubtaskAgentAttempts(IReadOnlyList<SupervisorDecisionRecord> decisions, long planSequence)
    {
        var map = new Dictionary<string, List<Guid>>();

        foreach (var d in decisions.Where(d => d.Sequence > planSequence && d.DecisionKind is SupervisorDecisionKinds.Spawn or SupervisorDecisionKinds.Retry).OrderBy(d => d.Sequence))
        {
            var agentIds = SupervisorOutcome.ReadStagedAgentRunIds(d.OutcomeJson);

            if (d.DecisionKind == SupervisorDecisionKinds.Spawn)
            {
                var subtaskIds = SupervisorOutcome.ReadSpawnSubtaskIds(d.PayloadJson);

                for (var i = 0; i < Math.Min(subtaskIds.Count, agentIds.Count); i++)
                    Append(map, subtaskIds[i], agentIds[i]);
            }
            else if (SupervisorOutcome.ReadRetrySubtaskId(d.PayloadJson) is { } retried && agentIds.Count > 0)
                Append(map, retried, agentIds[0]);
        }

        return map.ToDictionary(kv => kv.Key, kv => (IReadOnlyList<Guid>)kv.Value);
    }

    private static void Append(Dictionary<string, List<Guid>> map, string subtaskId, Guid agentId)
    {
        if (!map.TryGetValue(subtaskId, out var list)) map[subtaskId] = list = new List<Guid>();

        list.Add(agentId);
    }

    /// <summary>
    /// The per-agent ALLOCATION the model authored: <c>agentRunId → (role, subtask title)</c>. Joins the latest plan's
    /// subtasks (<c>subtaskId → title</c>) and the spawn's per-agent dispatch roles (<c>subtaskId → role</c>) onto each
    /// staged agent through the SAME <c>subtaskIds[i] ↔ agentRunIds[i]</c> staging order as <see cref="SubtaskAgentAttempts"/>
    /// — so a spawn fans the title+role onto exactly the agent that ran it. A retry re-runs ONE subtask as a fresh agent
    /// (carrying the subtask title; no per-agent role on a retry). Pure + best-effort — a homogeneous spawn (no
    /// <c>agents[]</c>) or a flat plan simply yields null role/title, byte-identical to before.
    /// </summary>
    /// <summary>A phase's status folds from its agents: none → Pending; any failed/cancelled/timed-out → Failed; all succeeded → Succeeded; else still Active.</summary>
    private static PhaseStatus PhaseStatusFromAgents(IReadOnlyList<PhaseAgentRef> agents)
    {
        if (agents.Count == 0) return PhaseStatus.Pending;

        if (agents.Any(a => a.Status is nameof(AgentRunStatus.Failed) or nameof(AgentRunStatus.Cancelled) or nameof(AgentRunStatus.TimedOut))) return PhaseStatus.Failed;

        return agents.All(a => a.Status == nameof(AgentRunStatus.Succeeded)) ? PhaseStatus.Succeeded : PhaseStatus.Active;
    }

    /// <summary>A phase's acceptance rendered for the board: the human description, else the argv command joined — null when the phase declared no acceptance.</summary>
    private static string? PhaseAcceptanceSummary(SupervisorAcceptanceSpec? acceptance) =>
        acceptance == null ? null : acceptance.Description ?? string.Join(" ", acceptance.Command);

    /// <summary>The REAL <c>AgentRun</c> rows for every id a spawn/retry decision staged, team-scoped — carrying the ground-truth status (mirrors <c>SupervisorScorecardService.SpawnedAgentStatusesByRunAsync</c>) PLUS the start/complete timestamps the live duration is computed from.</summary>
    private async Task<IReadOnlyList<AgentRunRow>> SpawnedAgentRowsAsync(Guid teamId, IReadOnlyList<Guid> ids, CancellationToken cancellationToken)
    {
        if (ids.Count == 0) return Array.Empty<AgentRunRow>();

        return await _db.AgentRun.AsNoTracking()
            .Where(r => r.TeamId == teamId && ids.Contains(r.Id))
            .Select(r => new AgentRunRow(r.Id, r.Status, r.StartedAt, r.CompletedAt))
            .ToListAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Builds the live per-agent extras (duration + tool count) from the agent rows — the duration + tool-count primitives are shared with the node source via <see cref="AgentMetricsReader"/> so the two can't diverge. Duration is final once terminal, else live elapsed at <paramref name="now"/>; null before it starts; tool count defaults to 0.</summary>
    private static IReadOnlyDictionary<Guid, AgentRunExtras> ExtrasById(IReadOnlyList<AgentRunRow> runs, IReadOnlyDictionary<Guid, int> toolCounts, DateTimeOffset now) =>
        runs.ToDictionary(r => r.Id, r => new AgentRunExtras { DurationMs = AgentMetricsReader.ComputeDuration(r.StartedAt, r.CompletedAt, now), ToolCount = toolCounts.GetValueOrDefault(r.Id) });

    private static RunPhase ToPhase(SupervisorDecisionRecord decision, AgentRollup rollup)
    {
        var agents = ChildAgentRefs(decision, rollup);

        return new RunPhase
        {
            Id = $"decision-{decision.Sequence}",
            Label = LabelFor(decision, agents.Count),
            Kind = decision.DecisionKind,
            Status = PhaseStatusMap.FromDecision(decision.Status),
            Order = OrderBase + (int)decision.Sequence,
            Agents = agents,
            Metrics = PhaseAgentMetrics.From(agents),
            Summary = SummaryFor(decision),
            SourceKey = Key,
            StartedAt = decision.CreatedDate,
            CompletedAt = SupervisorDecisionStateMachine.IsTerminal(decision.Status) ? decision.LastModifiedDate : null,
        };
    }

    /// <summary>For spawn/retry: the real agent refs the outcome staged, with the GROUND-TRUTH status (an unfound id — e.g. a Pending outcome with none yet — is simply omitted). Every other verb: no children.</summary>
    private static IReadOnlyList<PhaseAgentRef> ChildAgentRefs(SupervisorDecisionRecord decision, AgentRollup rollup) =>
        StagedAgentIds(decision)
            .Where(rollup.Knows)
            .Select(rollup.RefFor)
            .ToList();

    /// <summary>
    /// The per-agent lookup bundle threaded through every phase path — the ground-truth status map, the ledger-folded
    /// compact (model/tokens), and the live extras (duration/tool count) — with <see cref="RefFor"/> as the ONE place a
    /// <see cref="PhaseAgentRef"/> is assembled, so every source path (per-decision + authored phases) projects identically.
    /// </summary>
    private sealed class AgentRollup
    {
        private readonly IReadOnlyDictionary<Guid, AgentRunStatus> _status;
        private readonly IReadOnlyDictionary<Guid, SupervisorAgentResult> _compact;
        private readonly IReadOnlyDictionary<Guid, AgentRunExtras> _extras;
        private readonly IReadOnlyDictionary<Guid, AgentAllocation> _allocation;

        public AgentRollup(IReadOnlyDictionary<Guid, AgentRunStatus> status, IReadOnlyDictionary<Guid, SupervisorAgentResult> compact, IReadOnlyDictionary<Guid, AgentRunExtras> extras, IReadOnlyDictionary<Guid, AgentAllocation> allocation)
        {
            _status = status;
            _compact = compact;
            _extras = extras;
            _allocation = allocation;
        }

        /// <summary>True when the real agent row was found (status resolved) — the gate for emitting a ref.</summary>
        public bool Knows(Guid id) => _status.ContainsKey(id);

        /// <summary>An agent ref carrying its ground-truth status, the ledger compact (a blank model reads as null — no chip; cost = model×tokens, fail-open null when unpriced; files = the compact's git-truth changed-file count), and the live extras (absent → duration/tool null).</summary>
        public PhaseAgentRef RefFor(Guid id)
        {
            var compact = _compact.GetValueOrDefault(id);
            var extras = _extras.GetValueOrDefault(id);
            var allocation = _allocation.GetValueOrDefault(id);
            var model = string.IsNullOrWhiteSpace(compact?.Model) ? null : compact!.Model;

            return new PhaseAgentRef
            {
                AgentRunId = id,
                Status = _status[id].ToString(),
                Role = allocation?.Role,
                AssignedSubtask = allocation?.SubtaskTitle,
                Model = model,
                InputTokens = compact?.InputTokens,
                OutputTokens = compact?.OutputTokens,
                DurationMs = extras?.DurationMs,
                ToolCount = extras?.ToolCount,
                CostUsd = compact is null ? null : AgentCostPricing.CostUsd(model, compact.InputTokens, compact.OutputTokens),
                FilesChanged = compact?.ChangedFiles.Count,
                ChangedFiles = compact is null ? Array.Empty<string>() : compact.ChangedFiles.Take(MaxAgentFiles).ToList(),
                Summary = string.IsNullOrWhiteSpace(compact?.Summary) ? null : compact!.Summary,
            };
        }
    }

    /// <summary>A staged agent's real <c>AgentRun</c> row, narrowed to what the rollup needs — ground-truth status + the timestamps the live duration is computed from.</summary>
    private sealed record AgentRunRow(Guid Id, AgentRunStatus Status, DateTimeOffset? StartedAt, DateTimeOffset? CompletedAt);

    private static IReadOnlyList<Guid> StagedAgentIds(SupervisorDecisionRecord decision) =>
        SupervisorDecisionKinds.StagesAgents(decision.DecisionKind)
            ? SupervisorOutcome.ReadStagedAgentRunIds(decision.OutcomeJson)
            : Array.Empty<Guid>();


    private static string LabelFor(SupervisorDecisionRecord decision, int agentCount) => decision.DecisionKind switch
    {
        SupervisorDecisionKinds.Plan => "Plan",
        SupervisorDecisionKinds.Spawn => agentCount > 0 ? $"Spawn {agentCount} agents" : "Spawn",
        SupervisorDecisionKinds.Retry => "Retry",
        SupervisorDecisionKinds.AskHuman => "Ask human",
        SupervisorDecisionKinds.Merge => "Merge",
        SupervisorDecisionKinds.Resolve => agentCount > 0 ? "Resolve conflict" : "Resolve",
        SupervisorDecisionKinds.Stop => "Stop",
        _ => decision.DecisionKind,
    };

    /// <summary>For ask_human: the question, joined with the human's answer when one has been folded in. For stop: the model's closing summary (else the failure reason). Otherwise the failure reason (when terminal-failed). Null when neither.</summary>
    private static string? SummaryFor(SupervisorDecisionRecord decision)
    {
        if (decision.DecisionKind == SupervisorDecisionKinds.AskHuman) return AskHumanSummary(decision);

        if (decision.DecisionKind == SupervisorDecisionKinds.Stop) return SupervisorOutcome.ReadStopSummary(decision.OutcomeJson) ?? decision.Error;

        return decision.Error;
    }

    private static string? AskHumanSummary(SupervisorDecisionRecord decision)
    {
        var question = SupervisorOutcome.ReadAskHumanQuestion(decision.OutcomeJson);
        var answer = SupervisorOutcome.ReadAskHumanAnswer(decision.OutcomeJson);

        if (question == null) return decision.Error;

        return answer == null ? question : $"{question} — {answer}";
    }

    private static readonly IReadOnlyDictionary<Guid, AgentRunExtras> EmptyExtras = new Dictionary<Guid, AgentRunExtras>();
}
