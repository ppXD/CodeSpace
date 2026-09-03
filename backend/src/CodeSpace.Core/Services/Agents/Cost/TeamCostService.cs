using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Agents;
using Microsoft.EntityFrameworkCore;

namespace CodeSpace.Core.Services.Agents.Cost;

/// <summary>
/// Reads <c>agent_run</c> rows team-scoped, projects BOTH jsonb blobs (TaskJson for the model, ResultJson for the
/// captured TokenUsage), prices each via <see cref="AgentCostPricing"/>, and rolls the spend up per run + per team
/// (SOTA #4). Mirrors <c>SupervisorScorecardService</c> — thin (Rule 16), read-only, team-scoped.
///
/// <para>D1 — the bill covers BOTH lanes. It used to sum only <c>agent_run</c> rows, so every dollar the brain plane
/// spent (supervisor decisions, critic reviews, acceptance graders — the <c>interaction.completed</c> ledger rows) was
/// missing from the team's own bill. Those rows are now summed too and reported as <c>BrainPlaneUsd</c>, with
/// <c>TotalUsd</c> as the sum of the two; <c>EstimatedCostUsd</c> keeps its EXACT prior meaning (agent execution only)
/// so no already-displayed number silently changes what it counts. A run with ONLY brain spend (a supervisor that
/// never spawned an agent) now appears in the breakdown instead of vanishing from it.</para>
///
/// <para>HONEST: terminal-only (a non-null ResultJson means the run completed + persisted its result). A row whose
/// model/usage cannot be priced contributes 0 to the cost sum and increments the UnknownCostRuns qualifier
/// (fail-open — never silently $0, never blocks). A DB-load fault PROPAGATES (fail-closed — the query is not
/// try/caught, mirroring AdmissionController). A single malformed result row degrades to unknown, never crashes
/// the whole roll-up. The summed totals cover the FULL window; only the per-run breakdown is payload-bounded.</para>
/// </summary>
public sealed class TeamCostService : ITeamCostService, IScopedDependency
{
    /// <summary>Cap on the per-run breakdown returned in a rollup — bounds the payload (mirrors SupervisorScorecardService.RecentRunCap). The summed totals are NOT capped; only the Runs list is, with Truncated set.</summary>
    public const int RecentRunCap = 100;

    private readonly CodeSpaceDbContext _db;

    public TeamCostService(CodeSpaceDbContext db)
    {
        _db = db;
    }

    public async Task<TeamCostRollup> ComputeRollupAsync(Guid teamId, DateTimeOffset? since, CancellationToken cancellationToken)
    {
        var prices = await ModelPriceResolver.LoadAsync(_db, teamId, cancellationToken).ConfigureAwait(false);
        var rows = await TerminalRowsAsync(teamId, runId: null, since, cancellationToken).ConfigureAwait(false);
        var brain = await BrainSpendByRunAsync(teamId, runId: null, since, prices, cancellationToken).ConfigureAwait(false);

        var priced = rows.Select(r => Price(r, prices)).ToList();

        var byRecency = OrderRunsByRecency(priced, brain)
            .Select(runId => SummarizeRun(runId, priced.Where(p => p.WorkflowRunId == runId), brain))
            .ToList();

        return new TeamCostRollup
        {
            TotalInputTokens = priced.Sum(p => (long)p.InputTokens),
            TotalOutputTokens = priced.Sum(p => (long)p.OutputTokens),
            EstimatedCostUsd = SumKnown(priced),
            BrainPlaneUsd = SumBrain(brain.Values),
            TotalUsd = Combine(SumKnown(priced), SumBrain(brain.Values)),
            RunCount = Math.Min(byRecency.Count, RecentRunCap),
            UnknownCostRuns = priced.Count(p => p.Cost is null),
            WindowRunCount = byRecency.Count,
            Truncated = byRecency.Count > RecentRunCap,
            Runs = byRecency.Take(RecentRunCap).ToList(),
        };
    }

    public async Task<RunCostSummary> ComputeRunAsync(Guid teamId, Guid workflowRunId, CancellationToken cancellationToken)
    {
        var prices = await ModelPriceResolver.LoadAsync(_db, teamId, cancellationToken).ConfigureAwait(false);
        var rows = await TerminalRowsAsync(teamId, workflowRunId, since: null, cancellationToken).ConfigureAwait(false);
        var brain = await BrainSpendByRunAsync(teamId, workflowRunId, since: null, prices, cancellationToken).ConfigureAwait(false);

        return SummarizeRun(workflowRunId, rows.Select(r => Price(r, prices)), brain);
    }

    public async Task<IReadOnlyDictionary<Guid, RunCostSummary>> ComputeRunsAsync(Guid teamId, IReadOnlyCollection<Guid> workflowRunIds, CancellationToken cancellationToken)
    {
        if (workflowRunIds.Count == 0) return EmptySummaries;

        var prices = await ModelPriceResolver.LoadAsync(_db, teamId, cancellationToken).ConfigureAwait(false);

        var rows = await _db.AgentRun.AsNoTracking()
            .Where(r => r.TeamId == teamId && r.ResultJson != null && workflowRunIds.Contains(r.WorkflowRunId ?? r.Id))
            .Select(r => new CostRow { WorkflowRunId = r.WorkflowRunId ?? r.Id, ResultJson = r.ResultJson!, TaskJson = r.TaskJson, CreatedAt = r.CreatedDate })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var brainRecords = await TeamInteractionRecords(teamId)
            .Where(r => workflowRunIds.Contains(r.RunId))
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        var brain = FoldBrainRecords(brainRecords, prices);
        var priced = rows.Select(r => Price(r, prices)).ToList();

        // A run with ONLY brain spend still belongs in the answer — the caller asked what these runs cost, and
        // "no agent rows" is not "cost nothing".
        return priced.Select(p => p.WorkflowRunId).Concat(brain.Keys).Distinct()
            .ToDictionary(runId => runId, runId => SummarizeRun(runId, priced.Where(p => p.WorkflowRunId == runId), brain));
    }

    /// <summary>Team-scoped terminal agent rows (non-null ResultJson), projecting BOTH jsonb blobs + the timestamp for recency. The query is NOT try/caught — a load fault propagates (fail-closed).</summary>
    private async Task<IReadOnlyList<CostRow>> TerminalRowsAsync(Guid teamId, Guid? runId, DateTimeOffset? since, CancellationToken cancellationToken)
    {
        var query = _db.AgentRun.AsNoTracking()
            .Where(r => r.TeamId == teamId && r.ResultJson != null);

        if (runId is { } id) query = query.Where(r => r.WorkflowRunId == id);
        if (since is { } from) query = query.Where(r => r.CreatedDate >= from);

        // A STANDALONE agent run (no owning workflow) keys on its OWN id, so each forms its own singleton run in the
        // per-run breakdown rather than collapsing every standalone run of the team into one synthetic Guid.Empty
        // group. The summed totals are unaffected (they sum over the rows, not the grouping).
        return await query
            .Select(r => new CostRow { WorkflowRunId = r.WorkflowRunId ?? r.Id, ResultJson = r.ResultJson!, TaskJson = r.TaskJson, CreatedAt = r.CreatedDate })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// D1 — the BRAIN-PLANE half of the bill, per run: every <c>interaction.completed</c> ledger row of this team's
    /// runs (the supervisor's own decision calls, a critic's review, an acceptance-grading judge), priced by the SAME
    /// pricer the agent lane uses so a brain dollar and an agent dollar can never disagree. Team-scoping rides the
    /// owning <c>WorkflowRun</c> (the record table itself carries no team), so it is fail-closed like the agent query.
    /// </summary>
    private async Task<IReadOnlyDictionary<Guid, BrainSpend>> BrainSpendByRunAsync(Guid teamId, Guid? runId, DateTimeOffset? since, IReadOnlyDictionary<string, ModelPrice> prices, CancellationToken cancellationToken)
    {
        var query = TeamInteractionRecords(teamId);

        if (runId is { } id) query = query.Where(r => r.RunId == id);
        if (since is { } from) query = query.Where(r => r.OccurredAt >= from);

        var records = await query.ToListAsync(cancellationToken).ConfigureAwait(false);

        return FoldBrainRecords(records, prices);
    }

    private IQueryable<Persistence.Entities.WorkflowRunRecord> TeamInteractionRecords(Guid teamId) =>
        _db.WorkflowRunRecord.AsNoTracking()
            .Where(r => r.RecordType == WorkflowRunRecordTypes.InteractionCompleted
                        && _db.WorkflowRun.Any(w => w.Id == r.RunId && w.TeamId == teamId));

    /// <summary>Price the fetched interaction rows and group them by run. A row whose model is unpriceable contributes nothing to the sum and increments the run's unknown counter — the same fail-open honesty the agent lane keeps.</summary>
    private static IReadOnlyDictionary<Guid, BrainSpend> FoldBrainRecords(IReadOnlyList<Persistence.Entities.WorkflowRunRecord> records, IReadOnlyDictionary<string, ModelPrice> prices) =>
        records
            .GroupBy(r => r.RunId)
            .ToDictionary(g => g.Key, g =>
            {
                var rows = g.Select(r => new { Record = r, Spend = InteractionSpend.From(r, prices) }).ToList();
                var known = rows.Where(r => r.Spend.CostUsd is not null).ToList();

                return new BrainSpend
                {
                    LatestAt = g.Max(r => r.OccurredAt),
                    CostUsd = known.Count == 0 ? null : known.Sum(r => r.Spend.CostUsd!.Value),
                };
            });

    /// <summary>Every run that spent ANYTHING in either lane, most-recent-first over whichever lane last touched it.</summary>
    private static IEnumerable<Guid> OrderRunsByRecency(IReadOnlyList<PricedRow> priced, IReadOnlyDictionary<Guid, BrainSpend> brain) =>
        priced.Select(p => p.WorkflowRunId).Concat(brain.Keys).Distinct()
            .OrderByDescending(runId => Latest(priced, brain, runId));

    private static DateTimeOffset Latest(IReadOnlyList<PricedRow> priced, IReadOnlyDictionary<Guid, BrainSpend> brain, Guid runId)
    {
        var agentLatest = priced.Where(p => p.WorkflowRunId == runId).Select(p => p.CreatedAt).DefaultIfEmpty(DateTimeOffset.MinValue).Max();

        return brain.TryGetValue(runId, out var b) && b.LatestAt > agentLatest ? b.LatestAt : agentLatest;
    }

    /// <summary>Price ONE row: deserialize the model (TaskJson) + usage (ResultJson), defensively (a malformed row → unknown, never a throw), then cost via the pure pricer.</summary>
    private static PricedRow Price(CostRow row, IReadOnlyDictionary<string, ModelPrice> prices)
    {
        var result = TryDeserialize<AgentRunResult>(row.ResultJson);
        var task = TryDeserialize<AgentTask>(row.TaskJson);

        var input = result?.TokenUsage?.InputTokens ?? 0;
        var output = result?.TokenUsage?.OutputTokens ?? 0;
        var cost = result?.TokenUsage is null ? null : AgentCostPricing.CostUsd(task?.Model, input, output, prices);

        return new PricedRow { WorkflowRunId = row.WorkflowRunId, CreatedAt = row.CreatedAt, InputTokens = input, OutputTokens = output, Cost = cost };
    }

    private static RunCostSummary SummarizeRun(Guid runId, IEnumerable<PricedRow> rows, IReadOnlyDictionary<Guid, BrainSpend> brain)
    {
        var list = rows.ToList();
        var brainSpend = brain.TryGetValue(runId, out var b) ? b : null;
        var agentCost = SumKnown(list);

        return new RunCostSummary
        {
            WorkflowRunId = runId,
            SummedInputTokens = list.Sum(r => (long)r.InputTokens),
            SummedOutputTokens = list.Sum(r => (long)r.OutputTokens),
            EstimatedCostUsd = agentCost,
            BrainPlaneUsd = brainSpend?.CostUsd,
            TotalUsd = Combine(agentCost, brainSpend?.CostUsd),
            CountedRuns = list.Count,
            UnknownCostRuns = list.Count(r => r.Cost is null),
        };
    }

    /// <summary>The summed cost of the priced rows, or null when NONE was priceable (all unknown) — distinct from a real $0.</summary>
    private static decimal? SumKnown(IReadOnlyList<PricedRow> rows) =>
        rows.Any(r => r.Cost is not null) ? rows.Where(r => r.Cost is not null).Sum(r => r.Cost!.Value) : null;

    /// <summary>The summed brain-plane cost across runs, or null when no run had a priceable in-process call.</summary>
    private static decimal? SumBrain(IEnumerable<BrainSpend> spends)
    {
        var known = spends.Where(s => s.CostUsd is not null).ToList();

        return known.Count == 0 ? null : known.Sum(s => s.CostUsd!.Value);
    }

    /// <summary>Add two possibly-unknown lane totals. Null + null stays null (nothing was priceable — NOT a real $0); one known lane stands on its own.</summary>
    private static decimal? Combine(decimal? agent, decimal? brainPlane) =>
        agent is null && brainPlane is null ? null : (agent ?? 0m) + (brainPlane ?? 0m);

    private static T? TryDeserialize<T>(string? json) where T : class
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { return System.Text.Json.JsonSerializer.Deserialize<T>(json, AgentJson.Options); }
        catch { return null; }
    }

    private sealed record CostRow { public required Guid WorkflowRunId { get; init; } public required string ResultJson { get; init; } public required string TaskJson { get; init; } public DateTimeOffset CreatedAt { get; init; } }
    private sealed record PricedRow { public required Guid WorkflowRunId { get; init; } public DateTimeOffset CreatedAt { get; init; } public int InputTokens { get; init; } public int OutputTokens { get; init; } public decimal? Cost { get; init; } }
    private sealed record BrainSpend { public DateTimeOffset LatestAt { get; init; } public decimal? CostUsd { get; init; } }

    private static readonly IReadOnlyDictionary<Guid, RunCostSummary> EmptySummaries = new Dictionary<Guid, RunCostSummary>();
}
