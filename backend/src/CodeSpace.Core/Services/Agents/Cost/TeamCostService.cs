using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Messages.Agents;
using Microsoft.EntityFrameworkCore;

namespace CodeSpace.Core.Services.Agents.Cost;

/// <summary>
/// Reads <c>agent_run</c> rows team-scoped, projects BOTH jsonb blobs (TaskJson for the model, ResultJson for the
/// captured TokenUsage), prices each via <see cref="AgentCostPricing"/>, and rolls the spend up per run + per team
/// (SOTA #4). Mirrors <c>SupervisorScorecardService</c> — thin (Rule 16), read-only, team-scoped.
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
        var rows = await TerminalRowsAsync(teamId, runId: null, since, cancellationToken).ConfigureAwait(false);

        var priced = rows.Select(Price).ToList();

        var byRecency = priced
            .GroupBy(p => p.WorkflowRunId)
            .OrderByDescending(g => g.Max(p => p.CreatedAt))
            .Select(g => SummarizeRun(g.Key, g))
            .ToList();

        return new TeamCostRollup
        {
            TotalInputTokens = priced.Sum(p => (long)p.InputTokens),
            TotalOutputTokens = priced.Sum(p => (long)p.OutputTokens),
            EstimatedCostUsd = SumKnown(priced),
            RunCount = Math.Min(byRecency.Count, RecentRunCap),
            UnknownCostRuns = priced.Count(p => p.Cost is null),
            WindowRunCount = byRecency.Count,
            Truncated = byRecency.Count > RecentRunCap,
            Runs = byRecency.Take(RecentRunCap).ToList(),
        };
    }

    public async Task<RunCostSummary> ComputeRunAsync(Guid teamId, Guid workflowRunId, CancellationToken cancellationToken)
    {
        var rows = await TerminalRowsAsync(teamId, workflowRunId, since: null, cancellationToken).ConfigureAwait(false);

        return SummarizeRun(workflowRunId, rows.Select(Price));
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

    /// <summary>Price ONE row: deserialize the model (TaskJson) + usage (ResultJson), defensively (a malformed row → unknown, never a throw), then cost via the pure pricer.</summary>
    private static PricedRow Price(CostRow row)
    {
        var result = TryDeserialize<AgentRunResult>(row.ResultJson);
        var task = TryDeserialize<AgentTask>(row.TaskJson);

        var input = result?.TokenUsage?.InputTokens ?? 0;
        var output = result?.TokenUsage?.OutputTokens ?? 0;
        var cost = result?.TokenUsage is null ? null : AgentCostPricing.CostUsd(task?.Model, input, output);

        return new PricedRow { WorkflowRunId = row.WorkflowRunId, CreatedAt = row.CreatedAt, InputTokens = input, OutputTokens = output, Cost = cost };
    }

    private static RunCostSummary SummarizeRun(Guid runId, IEnumerable<PricedRow> rows)
    {
        var list = rows.ToList();

        return new RunCostSummary
        {
            WorkflowRunId = runId,
            SummedInputTokens = list.Sum(r => (long)r.InputTokens),
            SummedOutputTokens = list.Sum(r => (long)r.OutputTokens),
            EstimatedCostUsd = SumKnown(list),
            CountedRuns = list.Count,
            UnknownCostRuns = list.Count(r => r.Cost is null),
        };
    }

    /// <summary>The summed cost of the priced rows, or null when NONE was priceable (all unknown) — distinct from a real $0.</summary>
    private static decimal? SumKnown(IReadOnlyList<PricedRow> rows) =>
        rows.Any(r => r.Cost is not null) ? rows.Where(r => r.Cost is not null).Sum(r => r.Cost!.Value) : null;

    private static T? TryDeserialize<T>(string? json) where T : class
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { return System.Text.Json.JsonSerializer.Deserialize<T>(json, AgentJson.Options); }
        catch { return null; }
    }

    private sealed record CostRow { public required Guid WorkflowRunId { get; init; } public required string ResultJson { get; init; } public required string TaskJson { get; init; } public DateTimeOffset CreatedAt { get; init; } }
    private sealed record PricedRow { public required Guid WorkflowRunId { get; init; } public DateTimeOffset CreatedAt { get; init; } public int InputTokens { get; init; } public int OutputTokens { get; init; } public decimal? Cost { get; init; } }
}
