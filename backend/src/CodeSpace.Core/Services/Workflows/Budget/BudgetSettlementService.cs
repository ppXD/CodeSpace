using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Supervisor;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Enums;
using CodeSpace.Core.Services.Workflows.Engine;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CodeSpace.Core.Services.Workflows.Budget;

public interface IBudgetSettlementService
{
    /// <summary>Settle known folded agent-attempt costs, retain unknown or missing outcomes, release separate terminal map-branch claims and expire overdue reservations. Returns confirmed settlements, releases and expirations.</summary>
    Task<(int Settled, int Released, int Expired)> SweepAsync(int batchSize, CancellationToken cancellationToken);
}

/// <summary>
/// W-hard 2b: the settlement half of the atomic budget ledger — eventually-consistent by design (admission stays
/// conservative meanwhile; a confirmed cost can correct the estimate in either direction). The sweep
/// maps each live agent-attempt reservation back to its attempt through the TAPE's own ordered facts: a terminal
/// spawn/retry decision's staged agent ids are positional with the wave, and the reservation scope keys are the
/// per-spawn iteration keys ({node}#turn{N}#{k}) minted at admission — so results[k] settles reservation #k at the
/// priced actual. A terminal run with no folded outcome retains an uncertain claim: a missing ACK does not
/// prove that no provider was billed. Unknown claims remain eligible for late evidence and rotate by their last
/// inspection time, so an unresolved prefix does not permanently hide later reservations.
/// </summary>
public sealed class BudgetSettlementService : IBudgetSettlementService, IScopedDependency
{
    private readonly CodeSpaceDbContext _db;
    private readonly IBudgetLedger _ledger;
    private readonly ILogger<BudgetSettlementService> _logger;

    public BudgetSettlementService(CodeSpaceDbContext db, IBudgetLedger ledger, ILogger<BudgetSettlementService> logger)
    {
        _db = db;
        _ledger = ledger;
        _logger = logger;
    }

    public async Task<(int Settled, int Released, int Expired)> SweepAsync(int batchSize, CancellationToken cancellationToken)
    {
        var live = await _db.BudgetReservation.AsNoTracking()
            .Where(r => r.Kind == "agent-attempt" && (r.State == BudgetReservationStates.Reserved || r.State == BudgetReservationStates.InFlight || r.State == BudgetReservationStates.Indeterminate || r.State == BudgetReservationStates.Reconciled))
            .OrderBy(r => r.LastModifiedDate).ThenBy(r => r.Id)
            .Take(batchSize)
            .Select(r => new { r.WorkflowRunId, r.TeamId, r.ScopeKey })
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        var settled = 0;
        var released = 0;

        foreach (var byRun in live.GroupBy(r => (r.WorkflowRunId, r.TeamId)))
        {
            try
            {
                var (s, rel) = await SettleRunAsync(byRun.Key.WorkflowRunId, byRun.Key.TeamId, byRun.Select(r => r.ScopeKey).ToHashSet(StringComparer.Ordinal), cancellationToken).ConfigureAwait(false);
                settled += s;
                released += rel;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Budget settlement failed for run {RunId}; its reservations stay live for the next pass", byRun.Key.WorkflowRunId);
            }
        }

        released += await ReleaseTerminalMapBranchesAsync(batchSize, cancellationToken).ConfigureAwait(false);

        // Close recovery bookkeeping for llm:* claims that missed in-band settlement. Their actual stays
        // unknown and their estimates continue to count toward committed budget until confirmed evidence arrives.
        await _ledger.ReconcileDanglingAsync("llm:", batchSize, cancellationToken).ConfigureAwait(false);

        var expired = await _ledger.ExpireOverdueAsync(batchSize, cancellationToken).ConfigureAwait(false);

        return (settled, released, expired);
    }

    /// <summary>
    /// Return the headroom held by a TERMINAL run's map-branch reservations.
    ///
    /// <para>These are admission estimates, not billing records: a branch has no per-branch actual to settle to —
    /// the run's real spend is accounted through its interaction records and read by <c>ITeamCostService</c> — so
    /// what a reservation buys is the ceiling holding DURING the fan-out. Holding one past the run's end would
    /// permanently consume a team's headroom for work that has already finished and been billed elsewhere.</para>
    ///
    /// <para>Deliberately a separate pass from <see cref="SettleRunAsync"/>: that one maps agent-attempt
    /// reservations back to supervisor attempts through the decision tape, which a map branch has none of. Sharing
    /// it would mean teaching the deep lane's settlement about a lane it knows nothing about.</para>
    /// </summary>
    private async Task<int> ReleaseTerminalMapBranchesAsync(int batchSize, CancellationToken cancellationToken)
    {
        var live = await _db.BudgetReservation.AsNoTracking()
            .Where(r => r.Kind == WorkflowEngine.MapBranchReservationKind
                     && (r.State == BudgetReservationStates.Reserved || r.State == BudgetReservationStates.InFlight))
            .OrderBy(r => r.CreatedDate)
            .Take(batchSize)
            .Select(r => new { r.WorkflowRunId, r.TeamId, r.ScopeKey })
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        if (live.Count == 0) return 0;

        var terminalRunIds = await _db.WorkflowRun.AsNoTracking()
            .Where(r => live.Select(l => l.WorkflowRunId).Contains(r.Id)
                     && (r.Status == WorkflowRunStatus.Success || r.Status == WorkflowRunStatus.Failure || r.Status == WorkflowRunStatus.Cancelled))
            .Select(r => r.Id)
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        var terminal = terminalRunIds.ToHashSet();
        var released = 0;

        foreach (var row in live.Where(l => terminal.Contains(l.WorkflowRunId)))
        {
            await _ledger.ReleaseAsync(row.WorkflowRunId, row.TeamId, WorkflowEngine.MapBranchReservationKind, row.ScopeKey, cancellationToken).ConfigureAwait(false);
            released++;
        }

        return released;
    }

    private async Task<(int Settled, int Released)> SettleRunAsync(Guid runId, Guid teamId, HashSet<string> liveScopeKeys, CancellationToken cancellationToken)
    {
        // ALL decisions in ledger order: a decision's TURN NUMBER is its 0-based position in the full ledger
        // (TurnNumber = PriorDecisions.Count at decide time) — the same arithmetic admission used to mint
        // {node}#turn{N}#{k}, so the tail "#turn{i}#{k}" aligns a folded result with exactly its reservation.
        var decisions = await _db.SupervisorDecisionRecord.AsNoTracking()
            .Where(d => d.SupervisorRunId == runId && d.TeamId == teamId)
            .OrderBy(d => d.Sequence)
            .Select(d => new { d.DecisionKind, d.OutcomeJson })
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        var runIsTerminal = await _db.WorkflowRun.AsNoTracking()
            .Where(r => r.Id == runId)
            .Select(r => r.Status == WorkflowRunStatus.Success || r.Status == WorkflowRunStatus.Failure || r.Status == WorkflowRunStatus.Cancelled)
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);

        // D1: the team's own per-model prices, loaded ONCE per run. Without them a model priced only on its pool row
        // settles null → pessimistically AT the reservation, so a run's committed spend permanently over-counts what
        // it actually cost and the cap trips early. The sweep must price exactly the way the fold does.
        var modelPrices = await Agents.Cost.ModelPriceResolver.LoadAsync(_db, teamId, cancellationToken).ConfigureAwait(false);

        var settled = 0;
        var matchedKeys = new HashSet<string>(StringComparer.Ordinal);

        for (var turn = 0; turn < decisions.Count; turn++)
        {
            if (decisions[turn].DecisionKind is not (SupervisorDecisionKinds.Spawn or SupervisorDecisionKinds.Retry)) continue;

            var results = SupervisorOutcome.ReadAgentResults(decisions[turn].OutcomeJson);

            for (var k = 0; k < results.Count; k++)
            {
                var tail = $"#turn{turn}#{k}";
                var key = liveScopeKeys.FirstOrDefault(candidate => candidate.EndsWith(tail, StringComparison.Ordinal));

                if (key is null) continue;

                // A folded known price is an actual; an unknown model keeps the existing claim uncertain.
                // Compact legacy agent results already default missing usage to zero and do not carry completeness.
                // This pricer prevents arithmetic loss; full CLI usage provenance requires the harness receipt path.
                var actual = Agents.Cost.LlmUsageCost.Usd(results[k].Model, new Llm.LlmUsage { InputTokens = results[k].InputTokens, OutputTokens = results[k].OutputTokens }, modelPrices);

                await _ledger.SettleAsync(runId, teamId, "agent-attempt", key, actual, cancellationToken).ConfigureAwait(false);
                matchedKeys.Add(key);
                if (actual is not null) settled++;
            }
        }

        if (runIsTerminal)
            foreach (var orphanKey in liveScopeKeys.Except(matchedKeys))
            {
                // A missing outcome is also compatible with a billed attempt whose completion ACK was lost.
                // Keep uncertainty separate from workflow completion; terminal status does not prove no spend.
                await _ledger.SettleAsync(runId, teamId, "agent-attempt", orphanKey, null, cancellationToken).ConfigureAwait(false);
            }

        // Even an active attempt with no result must rotate through a bounded global sweep. This changes only
        // inspection time on still-unsettled rows, never an actual receipt or its state.
        await _db.BudgetReservation.Where(r => r.WorkflowRunId == runId && r.TeamId == teamId && r.Kind == "agent-attempt" && liveScopeKeys.Contains(r.ScopeKey) && r.State != BudgetReservationStates.Settled)
            .ExecuteUpdateAsync(setters => setters.SetProperty(r => r.LastModifiedDate, DateTimeOffset.UtcNow), cancellationToken).ConfigureAwait(false);

        return (settled, 0);
    }
}
