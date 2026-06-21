using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Supervisor;
using CodeSpace.Core.Services.Workflows;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Enums;
using Microsoft.EntityFrameworkCore;

namespace CodeSpace.Core.Services.Agents.Eval;

/// <summary>
/// Loads a team's supervisor runs (a run = a WorkflowRun with at least one <c>SupervisorDecisionRecord</c>),
/// projects each to a <see cref="SupervisorRunOutcome"/>, and hands them to the pure
/// <see cref="SupervisorEvalScorecard"/>. Thin (Rule 16) — the service owns only the team-scoped queries +
/// projection; all the scoring math is the pure scorer's.
///
/// <para>HONEST + read-only: a run is terminal only when its <c>WorkflowRun</c> reached a terminal status
/// (<see cref="WorkflowRunState.IsTerminal"/>) — an in-flight run is reported not-scored; the spawn success rate
/// folds the REAL <see cref="AgentRun.Status"/> of the spawned agents (read by the agent-run ids the spawn/retry
/// outcomes recorded), never the decider's self-report; time-to-stop comes from real <c>CreatedDate</c>
/// timestamps. No writes, no engine logic. The per-run list is capped most-recent-first to bound the payload.</para>
/// </summary>
public sealed class SupervisorScorecardService : ISupervisorScorecardService, IScopedDependency
{
    /// <summary>Cap on the recent supervisor runs scored + returned per call — bounds the payload + query cost. The roll-up is over exactly these runs (an operator windows further with <c>since</c>).</summary>
    public const int RecentRunCap = 100;

    private readonly CodeSpaceDbContext _db;

    public SupervisorScorecardService(CodeSpaceDbContext db)
    {
        _db = db;
    }

    public async Task<SupervisorScorecard> ComputeAsync(Guid teamId, DateTimeOffset? since, CancellationToken cancellationToken)
    {
        var runIds = await RecentSupervisorRunIdsAsync(teamId, since, cancellationToken).ConfigureAwait(false);

        if (runIds.Count == 0) return Empty();

        var decisionsByRun = await DecisionsByRunAsync(teamId, runIds, cancellationToken).ConfigureAwait(false);
        var runStatusById = await RunTerminalStateAsync(teamId, runIds, cancellationToken).ConfigureAwait(false);
        var spawnedStatusById = await SpawnedAgentStatusesByRunAsync(teamId, decisionsByRun, cancellationToken).ConfigureAwait(false);

        var outcomes = runIds
            .Select(runId => ProjectRun(runId, decisionsByRun.GetValueOrDefault(runId, EmptyDecisions), runStatusById.GetValueOrDefault(runId), spawnedStatusById.GetValueOrDefault(runId, EmptyStatuses)))
            .ToList();

        return SupervisorEvalScorecard.Compute(outcomes);
    }

    /// <summary>
    /// The team's recent supervisor run ids (most-recent first by FIRST-decision time), capped at
    /// <see cref="RecentRunCap"/> and windowed by <paramref name="since"/> on the run's first decision. A
    /// supervisor run is any run with at least one team-scoped decision row — derived purely from the ledger, so a
    /// flag-OFF deployment (no decisions ever written) yields none and the scorecard is empty.
    /// </summary>
    private async Task<IReadOnlyList<Guid>> RecentSupervisorRunIdsAsync(Guid teamId, DateTimeOffset? since, CancellationToken cancellationToken)
    {
        var grouped = _db.SupervisorDecisionRecord.AsNoTracking()
            .Where(d => d.TeamId == teamId)
            .GroupBy(d => d.SupervisorRunId)
            .Select(g => new { RunId = g.Key, FirstAt = g.Min(d => d.CreatedDate) });

        if (since is { } from) grouped = grouped.Where(g => g.FirstAt >= from);

        return await grouped
            .OrderByDescending(g => g.FirstAt)
            .Take(RecentRunCap)
            .Select(g => g.RunId)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>All decision rows for the in-scope runs, team-scoped, grouped by run in <c>Sequence</c> order (the replay tape).</summary>
    private async Task<IReadOnlyDictionary<Guid, IReadOnlyList<SupervisorDecisionRecord>>> DecisionsByRunAsync(Guid teamId, IReadOnlyList<Guid> runIds, CancellationToken cancellationToken)
    {
        var rows = await _db.SupervisorDecisionRecord.AsNoTracking()
            .Where(d => d.TeamId == teamId && runIds.Contains(d.SupervisorRunId))
            .OrderBy(d => d.Sequence)
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        return rows
            .GroupBy(r => r.SupervisorRunId)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<SupervisorDecisionRecord>)g.ToList());
    }

    /// <summary>The terminal state (the REAL terminal status — Success/Failure/Cancelled — else null, plus CompletedAt) of each in-scope supervisor run, team-scoped. A run absent here (no WorkflowRun row) or a non-terminal one is treated as in-flight (null status). Carrying the real status (not a lossy bool) lets the scorer classify a terminal run with no supervisor stop decision honestly — a Failure/Cancelled run never masquerades as completed.</summary>
    private async Task<IReadOnlyDictionary<Guid, RunTerminalState>> RunTerminalStateAsync(Guid teamId, IReadOnlyList<Guid> runIds, CancellationToken cancellationToken)
    {
        var rows = await _db.WorkflowRun.AsNoTracking()
            .Where(r => r.TeamId == teamId && runIds.Contains(r.Id))
            .Select(r => new { r.Id, r.Status, r.CompletedAt })
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        return rows.ToDictionary(r => r.Id, r => new RunTerminalState(WorkflowRunState.IsTerminal(r.Status) ? r.Status : null, r.CompletedAt));
    }

    /// <summary>
    /// The REAL terminal status of every agent each run spawned, keyed by run. The spawned agent-run ids come from
    /// the spawn/retry decisions' recorded outcomes (the honest ledger fact); their status is read fresh from the
    /// <c>AgentRun</c> rows, team-scoped — so a spawned agent that's still in flight (or failed) lowers the spawn
    /// success rate truthfully rather than being taken as a success on the decider's word.
    /// </summary>
    private async Task<IReadOnlyDictionary<Guid, IReadOnlyList<AgentRunStatus>>> SpawnedAgentStatusesByRunAsync(Guid teamId, IReadOnlyDictionary<Guid, IReadOnlyList<SupervisorDecisionRecord>> decisionsByRun, CancellationToken cancellationToken)
    {
        var agentIdsByRun = decisionsByRun.ToDictionary(kvp => kvp.Key, kvp => SpawnedAgentIds(kvp.Value));

        var allAgentIds = agentIdsByRun.Values.SelectMany(ids => ids).Distinct().ToList();

        if (allAgentIds.Count == 0) return new Dictionary<Guid, IReadOnlyList<AgentRunStatus>>();

        var statusById = (await _db.AgentRun.AsNoTracking()
                .Where(r => r.TeamId == teamId && allAgentIds.Contains(r.Id))
                .Select(r => new { r.Id, r.Status })
                .ToListAsync(cancellationToken).ConfigureAwait(false))
            .ToDictionary(r => r.Id, r => r.Status);

        return agentIdsByRun.ToDictionary(
            kvp => kvp.Key,
            kvp => (IReadOnlyList<AgentRunStatus>)kvp.Value.Where(statusById.ContainsKey).Select(id => statusById[id]).ToList());
    }

    /// <summary>The agent-run ids a run spawned, gathered from every spawn/retry decision's recorded outcome (the same ids a merge reads).</summary>
    private static IReadOnlyList<Guid> SpawnedAgentIds(IReadOnlyList<SupervisorDecisionRecord> decisions) =>
        decisions
            .Where(d => SupervisorDecisionKinds.StagesAgents(d.DecisionKind))
            .SelectMany(d => SupervisorOutcome.ReadStagedAgentRunIds(d.OutcomeJson))
            .ToList();

    /// <summary>Project one run's ledger + run-state + spawned-agent terminals into the pure scorer's input noun. Time-to-stop = first-decision → terminal-stop time (the last stop's CreatedDate, else the run's CompletedAt), only for a terminal run.</summary>
    private static SupervisorRunOutcome ProjectRun(Guid runId, IReadOnlyList<SupervisorDecisionRecord> decisions, RunTerminalState runState, IReadOnlyList<AgentRunStatus> spawnedStatuses) =>
        new()
        {
            SupervisorRunId = runId,
            Decisions = decisions.Select(ToSummary).ToList(),
            SpawnedAgentStatuses = spawnedStatuses,
            TerminalStatus = runState.TerminalStatus,
            TimeToStopSeconds = runState.TerminalStatus is not null ? TimeToStopSeconds(decisions, runState.CompletedAt) : null,
        };

    private static SupervisorDecisionSummary ToSummary(SupervisorDecisionRecord row) => new()
    {
        Kind = row.DecisionKind,
        StagedAgentCount = SupervisorDecisionKinds.StagesAgents(row.DecisionKind) ? SupervisorOutcome.ReadStagedAgentCount(row.OutcomeJson) : 0,
        StopReason = row.DecisionKind == SupervisorDecisionKinds.Stop ? ReadStopReason(row.PayloadJson) : null,
        // The OBJECTIVE acceptance verdict L4 P1 folded onto the stop's OUTCOME (not its payload). null for a stop with
        // no model definition-of-done — leaves the label-based classification byte-identical for every pre-acceptance run.
        AcceptancePassed = row.DecisionKind == SupervisorDecisionKinds.Stop ? SupervisorOutcome.ReadAcceptanceGradePassed(row.OutcomeJson) : null,
    };

    /// <summary>Wall-clock seconds from the first decision to the run's stop: the last stop decision's <c>CreatedDate</c> (the terminal decision) else the run's <c>CompletedAt</c>; null when neither anchor exists.</summary>
    private static double? TimeToStopSeconds(IReadOnlyList<SupervisorDecisionRecord> decisions, DateTimeOffset? completedAt)
    {
        if (decisions.Count == 0) return null;

        var start = decisions[0].CreatedDate;

        var stopAt = decisions.LastOrDefault(d => d.DecisionKind == SupervisorDecisionKinds.Stop)?.CreatedDate ?? completedAt;

        return stopAt is { } end && end >= start ? (end - start).TotalSeconds : null;
    }

    /// <summary>Read a stop decision's terminal label: a decider stop carries <c>outcome</c>; a forced-bound/governance stop carries <c>reason</c>. Best-effort — null when neither is present.</summary>
    private static string? ReadStopReason(string payloadJson)
    {
        try
        {
            var root = System.Text.Json.JsonDocument.Parse(payloadJson).RootElement;

            if (root.ValueKind != System.Text.Json.JsonValueKind.Object) return null;

            if (root.TryGetProperty("reason", out var reason) && reason.ValueKind == System.Text.Json.JsonValueKind.String) return reason.GetString();

            return root.TryGetProperty("outcome", out var outcome) && outcome.ValueKind == System.Text.Json.JsonValueKind.String ? outcome.GetString() : null;
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }

    private static SupervisorScorecard Empty() => new()
    {
        Rollup = new SupervisorRollup
        {
            ScoredRuns = 0,
            NotScoredRuns = 0,
            AvgDecisionsPerRun = 0,
            AvgReplanRounds = 0,
            OverallSpawnSuccessRate = 0,
            OutcomeDistribution = new Dictionary<string, int>(),
        },
        Runs = Array.Empty<SupervisorRunScore>(),
    };

    /// <summary>The run's REAL terminal status (Success/Failure/Cancelled) — null while in flight (the honest in-flight gate) — and when it completed.</summary>
    private readonly record struct RunTerminalState(WorkflowRunStatus? TerminalStatus, DateTimeOffset? CompletedAt);

    private static readonly IReadOnlyList<SupervisorDecisionRecord> EmptyDecisions = Array.Empty<SupervisorDecisionRecord>();
    private static readonly IReadOnlyList<AgentRunStatus> EmptyStatuses = Array.Empty<AgentRunStatus>();
}
