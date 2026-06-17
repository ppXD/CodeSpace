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

    /// <summary>The pure projection step — decisions + the already-resolved ground-truth agent statuses → phases. Separated from the DB read so it is unit-testable without a DbContext.</summary>
    public static IReadOnlyList<RunPhase> ProjectDecisions(IReadOnlyList<SupervisorDecisionRecord> decisions, IReadOnlyDictionary<Guid, AgentRunStatus> agentStatusById) =>
        decisions.Select(d => ToPhase(d, agentStatusById)).ToList();

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
