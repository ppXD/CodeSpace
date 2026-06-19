using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Supervisor;
using CodeSpace.Messages.Agents;
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

        var agentStatusById = await SpawnedAgentStatusesAsync(context.TeamId, decisions, cancellationToken).ConfigureAwait(false);

        return ProjectDecisions(decisions, agentStatusById);
    }

    /// <summary>High base offset for the model-authored SEMANTIC PHASES (L4 arc C) — they sort AFTER both the structural node phases AND the per-decision tape, as their own top-level band on the board.</summary>
    public const int PhaseOrderBase = 2_000_000;

    /// <summary>The pure projection step — decisions + the already-resolved ground-truth agent statuses → phases. Separated from the DB read so it is unit-testable without a DbContext. One phase per decision, PLUS the model-authored semantic phases (L4 arc C) when the plan grouped its subtasks; a flat plan adds none (the per-decision board verbatim).</summary>
    public static IReadOnlyList<RunPhase> ProjectDecisions(IReadOnlyList<SupervisorDecisionRecord> decisions, IReadOnlyDictionary<Guid, AgentRunStatus> agentStatusById)
    {
        var phases = decisions.Select(d => ToPhase(d, agentStatusById)).ToList();

        phases.AddRange(AuthoredPhases(decisions, agentStatusById));

        return phases;
    }

    /// <summary>
    /// L4 arc C: the model-authored semantic phases off the <c>plan</c> decision, projected as their OWN
    /// <see cref="RunPhase"/>s so the board reads "Investigate / Implement / Review" rather than a flat decision tape.
    /// Additive — a flat plan (no phases) contributes none. Each phase's children are the agents the spawns staged for
    /// its grouped subtasks (a phase's <c>subtaskIds</c> mapped through the spawn payload's <c>subtaskIds[i]</c> ↔ outcome
    /// <c>agentRunIds[i]</c> staging order), with the ground-truth status; its status folds from those children.
    /// </summary>
    private static IReadOnlyList<RunPhase> AuthoredPhases(IReadOnlyList<SupervisorDecisionRecord> decisions, IReadOnlyDictionary<Guid, AgentRunStatus> agentStatusById)
    {
        // The LATEST plan — matching the executor, which resolves a spawn's subtasks from the most recent plan
        // (ResolvePlannedSubtasks). On a re-plan, the phases must track the same plan the spawns were built from.
        var plan = decisions.LastOrDefault(d => d.DecisionKind == SupervisorDecisionKinds.Plan);
        var authored = SupervisorOutcome.ReadPlanPhases(plan?.OutcomeJson);

        if (authored.Count == 0) return Array.Empty<RunPhase>();

        var subtaskAgents = SubtaskAgentMap(decisions);

        return authored.Select((phase, index) => ToAuthoredPhase(phase, index, plan!, subtaskAgents, agentStatusById)).ToList();
    }

    private static RunPhase ToAuthoredPhase(SupervisorPlanPhase phase, int index, SupervisorDecisionRecord plan, IReadOnlyDictionary<string, Guid> subtaskAgents, IReadOnlyDictionary<Guid, AgentRunStatus> agentStatusById)
    {
        var agents = phase.SubtaskIds
            .Select(subtaskAgents.GetValueOrDefault)
            .Where(id => id != Guid.Empty && agentStatusById.ContainsKey(id))
            .Select(id => new PhaseAgentRef { AgentRunId = id, Status = agentStatusById[id].ToString() })
            .ToList();

        return new RunPhase
        {
            Id = $"phase-{phase.Id}",
            Label = phase.Title,
            Kind = "phase",
            Status = PhaseStatusFromAgents(agents),
            Order = PhaseOrderBase + index,
            Agents = agents,
            Metrics = MetricsFor(agents),
            Summary = PhaseAcceptanceSummary(phase.Acceptance),
            SourceKey = Key,
            StartedAt = plan.CreatedDate,
            CompletedAt = null,
        };
    }

    /// <summary>
    /// subtaskId → the agent-run id that ran it, walking <c>spawn</c> AND <c>retry</c> decisions in Sequence order so the
    /// CHRONOLOGICALLY-LATEST attempt wins (a spawn fans the payload <c>subtaskIds[i]</c> ↔ outcome <c>agentRunIds[i]</c>;
    /// a retry re-runs ONE subtask as a fresh agent). So a subtask that failed and was retried shows the retry's fresh
    /// agent (ground-truth), not the original failed one.
    /// </summary>
    private static IReadOnlyDictionary<string, Guid> SubtaskAgentMap(IReadOnlyList<SupervisorDecisionRecord> decisions)
    {
        var map = new Dictionary<string, Guid>();

        foreach (var d in decisions.Where(d => d.DecisionKind is SupervisorDecisionKinds.Spawn or SupervisorDecisionKinds.Retry).OrderBy(d => d.Sequence))
        {
            var agentIds = SupervisorOutcome.ReadStagedAgentRunIds(d.OutcomeJson);

            if (d.DecisionKind == SupervisorDecisionKinds.Spawn)
            {
                var subtaskIds = SupervisorOutcome.ReadSpawnSubtaskIds(d.PayloadJson);

                for (var i = 0; i < Math.Min(subtaskIds.Count, agentIds.Count); i++)
                    map[subtaskIds[i]] = agentIds[i];
            }
            else if (SupervisorOutcome.ReadRetrySubtaskId(d.PayloadJson) is { } retried && agentIds.Count > 0)
                map[retried] = agentIds[0];
        }

        return map;
    }

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

    /// <summary>The REAL terminal status of every agent any spawn/retry decision staged, keyed by id, team-scoped — the ground-truth fold (mirrors <c>SupervisorScorecardService.SpawnedAgentStatusesByRunAsync</c>).</summary>
    private async Task<IReadOnlyDictionary<Guid, AgentRunStatus>> SpawnedAgentStatusesAsync(Guid teamId, IReadOnlyList<SupervisorDecisionRecord> decisions, CancellationToken cancellationToken)
    {
        var ids = decisions.SelectMany(d => StagedAgentIds(d)).Distinct().ToList();

        if (ids.Count == 0) return EmptyStatuses;

        return (await _db.AgentRun.AsNoTracking()
                .Where(r => r.TeamId == teamId && ids.Contains(r.Id))
                .Select(r => new { r.Id, r.Status })
                .ToListAsync(cancellationToken).ConfigureAwait(false))
            .ToDictionary(r => r.Id, r => r.Status);
    }

    private static RunPhase ToPhase(SupervisorDecisionRecord decision, IReadOnlyDictionary<Guid, AgentRunStatus> agentStatusById)
    {
        var agents = ChildAgentRefs(decision, agentStatusById);

        return new RunPhase
        {
            Id = $"decision-{decision.Sequence}",
            Label = LabelFor(decision, agents.Count),
            Kind = decision.DecisionKind,
            Status = PhaseStatusMap.FromDecision(decision.Status),
            Order = OrderBase + (int)decision.Sequence,
            Agents = agents,
            Metrics = MetricsFor(agents),
            Summary = SummaryFor(decision),
            SourceKey = Key,
            StartedAt = decision.CreatedDate,
            CompletedAt = SupervisorDecisionStateMachine.IsTerminal(decision.Status) ? decision.LastModifiedDate : null,
        };
    }

    /// <summary>For spawn/retry: the real agent refs the outcome staged, with the GROUND-TRUTH status (an unfound id — e.g. a Pending outcome with none yet — is simply omitted). Every other verb: no children.</summary>
    private static IReadOnlyList<PhaseAgentRef> ChildAgentRefs(SupervisorDecisionRecord decision, IReadOnlyDictionary<Guid, AgentRunStatus> agentStatusById) =>
        StagedAgentIds(decision)
            .Where(agentStatusById.ContainsKey)
            .Select(id => new PhaseAgentRef { AgentRunId = id, Status = agentStatusById[id].ToString() })
            .ToList();

    private static IReadOnlyList<Guid> StagedAgentIds(SupervisorDecisionRecord decision) =>
        SupervisorDecisionKinds.StagesAgents(decision.DecisionKind)
            ? SupervisorOutcome.ReadStagedAgentRunIds(decision.OutcomeJson)
            : Array.Empty<Guid>();

    private static PhaseMetrics MetricsFor(IReadOnlyList<PhaseAgentRef> agents) => new()
    {
        AgentCount = agents.Count,
        SucceededCount = agents.Count(a => a.Status == nameof(AgentRunStatus.Succeeded)),
        FailedCount = agents.Count(a => a.Status is nameof(AgentRunStatus.Failed) or nameof(AgentRunStatus.Cancelled) or nameof(AgentRunStatus.TimedOut)),
    };

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

    /// <summary>For ask_human: the question, joined with the human's answer when one has been folded in. Otherwise the failure reason (when terminal-failed). Null when neither.</summary>
    private static string? SummaryFor(SupervisorDecisionRecord decision)
    {
        if (decision.DecisionKind == SupervisorDecisionKinds.AskHuman) return AskHumanSummary(decision);

        return decision.Error;
    }

    private static string? AskHumanSummary(SupervisorDecisionRecord decision)
    {
        var question = SupervisorOutcome.ReadAskHumanQuestion(decision.OutcomeJson);
        var answer = SupervisorOutcome.ReadAskHumanAnswer(decision.OutcomeJson);

        if (question == null) return decision.Error;

        return answer == null ? question : $"{question} — {answer}";
    }

    private static readonly IReadOnlyDictionary<Guid, AgentRunStatus> EmptyStatuses = new Dictionary<Guid, AgentRunStatus>();
}
