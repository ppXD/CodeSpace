using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Services.Agents.Cost;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Enums;
using Microsoft.EntityFrameworkCore;

namespace CodeSpace.Core.Services.Agents.Eval;

/// <summary>
/// Loads a team's PERSONA-attributed agent runs (those with an <c>AgentDefinitionId</c>, optionally windowed),
/// projects each to an <see cref="AgentRunStatSample"/> — pricing its spend the same way <c>TeamCostService</c> does
/// (both jsonb blobs, the pure <see cref="AgentCostPricing"/>) — and hands them to the pure
/// <see cref="AgentStatsScorer"/>. Thin (Rule 16): the service owns only the team-scoped query + projection; all the
/// grouping/scoring math is the pure scorer's.
///
/// <para>Mirrors <see cref="AgentRunScorecardService"/> (same load-to-memory + project + pure-scorer shape) and
/// <c>TeamCostService</c> (same defensive per-row pricing — a malformed result row degrades to unknown-cost, never
/// crashes the roll-up). A DB-load fault propagates (fail-closed). Runs with no <c>AgentDefinitionId</c> (pure-inline
/// runs) are excluded — they belong to no persona.</para>
/// </summary>
public sealed class AgentStatsService : IAgentStatsService, IScopedDependency
{
    private readonly CodeSpaceDbContext _db;

    public AgentStatsService(CodeSpaceDbContext db)
    {
        _db = db;
    }

    public async Task<AgentStatsRollup> ComputeAsync(Guid teamId, DateTimeOffset? since, CancellationToken cancellationToken)
    {
        var query = _db.AgentRun.AsNoTracking()
            .Where(r => r.TeamId == teamId && r.AgentDefinitionId != null);

        if (since is { } from) query = query.Where(r => r.CreatedDate >= from);

        var rows = await query
            .Select(r => new StatRow
            {
                AgentDefinitionId = r.AgentDefinitionId!.Value,
                Status = r.Status,
                StartedAt = r.StartedAt,
                CompletedAt = r.CompletedAt,
                CreatedAt = r.CreatedDate,
                ResultJson = r.ResultJson,
                TaskJson = r.TaskJson,
            })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var samples = rows.Select(Project).ToList();

        return AgentStatsScorer.Compute(samples);
    }

    /// <summary>Project ONE row to a sample: derive the duration + price the spend (both jsonb blobs → the pure pricer), defensively (a malformed row degrades to unknown-cost, never a throw). Mirrors <c>TeamCostService.Price</c>.</summary>
    private static AgentRunStatSample Project(StatRow row)
    {
        var duration = row.StartedAt is { } started && row.CompletedAt is { } completed
            ? (completed - started).TotalSeconds
            : (double?)null;

        // A run participates in the spend accounting only once it has persisted a result (ResultJson present) —
        // the same eligibility gate TeamCostService applies by pre-filtering ResultJson != null. An in-flight run
        // contributes nothing to cost (and is NOT counted as unknown-cost).
        var eligible = row.ResultJson != null;

        var result = TryDeserialize<AgentRunResult>(row.ResultJson);
        var task = TryDeserialize<AgentTask>(row.TaskJson);

        var input = result?.TokenUsage?.InputTokens ?? 0;
        var output = result?.TokenUsage?.OutputTokens ?? 0;
        var cost = result?.TokenUsage is null ? null : AgentCostPricing.CostUsd(task?.Model, input, output);

        return new AgentRunStatSample
        {
            AgentDefinitionId = row.AgentDefinitionId,
            Status = row.Status,
            DurationSeconds = duration,
            CostEligible = eligible,
            Cost = cost,
            CreatedAt = row.CreatedAt,
        };
    }

    private static T? TryDeserialize<T>(string? json) where T : class
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { return System.Text.Json.JsonSerializer.Deserialize<T>(json, AgentJson.Options); }
        catch { return null; }
    }

    private sealed record StatRow
    {
        public required Guid AgentDefinitionId { get; init; }
        public required AgentRunStatus Status { get; init; }
        public DateTimeOffset? StartedAt { get; init; }
        public DateTimeOffset? CompletedAt { get; init; }
        public required DateTimeOffset CreatedAt { get; init; }
        public string? ResultJson { get; init; }
        public required string TaskJson { get; init; }
    }
}
