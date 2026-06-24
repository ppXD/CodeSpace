using System.Text.Json;
using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Decisions;
using CodeSpace.Messages.Enums;
using Microsoft.EntityFrameworkCore;

namespace CodeSpace.Core.Services.Tasks.Phases;

/// <summary>
/// Reads the per-agent metrics (status · duration · tokens · tool count · model) for a set of agent runs, team-scoped, in
/// TWO batch queries — the <c>AgentRun</c> rows + a grouped <c>tool_call_ledger</c> count. The ONE place that turns the
/// durable agent record into <see cref="AgentRunMetrics"/>, so a plain <c>agent.code</c> / map agent surfaces the SAME
/// rollup <c>SupervisorPhaseSource</c> folds from its decision ledger. Duration is LIVE (recomputed at <c>now</c>);
/// tokens/model deserialize DEFENSIVELY from a NARROW projection of <c>ResultJson</c>/<c>TaskJson</c> — just the
/// token-usage + model leaves, never the whole result/task graph (so the heavy ChangedFiles/Summary/patch fields aren't
/// materialized on this poll-path) — and a malformed/partial blob reads as unknown, never throws, so one bad row can't
/// blank the whole projection. READ-ONLY.
/// </summary>
public sealed class AgentMetricsReader : IScopedDependency
{
    private readonly CodeSpaceDbContext _db;

    public AgentMetricsReader(CodeSpaceDbContext db)
    {
        _db = db;
    }

    public async Task<IReadOnlyDictionary<Guid, AgentRunMetrics>> ReadAsync(Guid teamId, IReadOnlyCollection<Guid> agentRunIds, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (agentRunIds.Count == 0) return Empty;

        var ids = agentRunIds as IReadOnlyList<Guid> ?? agentRunIds.ToList();

        var rows = await _db.AgentRun.AsNoTracking()
            .Where(r => r.TeamId == teamId && ids.Contains(r.Id))
            .Select(r => new Row(r.Id, r.Status, r.StartedAt, r.CompletedAt, r.ResultJson, r.TaskJson))
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        var toolCounts = await ToolCountsAsync(teamId, ids, cancellationToken).ConfigureAwait(false);

        return rows.ToDictionary(r => r.Id, r => Build(r.Id, r.Status, r.StartedAt, r.CompletedAt, r.ResultJson, r.TaskJson, toolCounts.GetValueOrDefault(r.Id), now));
    }

    /// <summary>How many SIDE-EFFECTING tool calls each agent made — its <c>tool_call_ledger</c> rows team-scoped, EXCLUDING the <c>decision.request</c> HITL envelopes (mirrors <c>SupervisorPhaseSource</c>). An agent with no rows is absent → 0 downstream.</summary>
    private async Task<IReadOnlyDictionary<Guid, int>> ToolCountsAsync(Guid teamId, IReadOnlyList<Guid> ids, CancellationToken cancellationToken) =>
        await _db.ToolCallLedger.AsNoTracking()
            .Where(t => t.TeamId == teamId && ids.Contains(t.AgentRunId) && t.ToolKind != DecisionToolKinds.DecisionRequest)
            .GroupBy(t => t.AgentRunId)
            .Select(g => new { AgentRunId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.AgentRunId, x => x.Count, cancellationToken).ConfigureAwait(false);

    /// <summary>Turn one agent's persisted state into the metrics bundle — pure, so the live clock + the defensive parses live in one unit-testable place. Tokens null until the result lands; model null when the task left it blank.</summary>
    public static AgentRunMetrics Build(Guid id, AgentRunStatus status, DateTimeOffset? startedAt, DateTimeOffset? completedAt, string? resultJson, string? taskJson, int toolCount, DateTimeOffset now)
    {
        var tokens = TryDeserialize<TokenSlice>(resultJson)?.TokenUsage;
        var model = TryDeserialize<ModelSlice>(taskJson)?.Model;

        return new AgentRunMetrics
        {
            Status = status,
            DurationMs = DurationMs(startedAt, completedAt, now),
            InputTokens = tokens?.InputTokens,
            OutputTokens = tokens?.OutputTokens,
            ToolCount = toolCount,
            Model = string.IsNullOrWhiteSpace(model) ? null : model,
        };
    }

    /// <summary>Live duration: final once terminal (<c>CompletedAt − StartedAt</c>), else elapsed (<c>now − StartedAt</c>); null before it starts; a negative span (clock skew) clamps to 0. Mirrors <c>SupervisorPhaseSource.DurationMs</c>.</summary>
    private static long? DurationMs(DateTimeOffset? startedAt, DateTimeOffset? completedAt, DateTimeOffset now)
    {
        if (startedAt is null) return null;

        var ms = (long)((completedAt ?? now) - startedAt.Value).TotalMilliseconds;

        return ms < 0 ? 0 : ms;
    }

    /// <summary>Defensive deserialize — a null/empty/malformed/partial blob reads as null, never throws (catches <see cref="JsonException"/>, the only data-shaped failure for these plain projection types under <c>AgentJson.Options</c>).</summary>
    private static T? TryDeserialize<T>(string? json) where T : class
    {
        if (string.IsNullOrEmpty(json)) return null;

        try { return JsonSerializer.Deserialize<T>(json, AgentJson.Options); }
        catch (JsonException) { return null; }
    }

    /// <summary>The token-usage leaf of <c>AgentRunResult</c> — a narrow projection so the result blob's heavy fields (patch / changed files / summary / transcript) are never materialized on this poll-path.</summary>
    private sealed record TokenSlice(AgentTokenUsage? TokenUsage);

    /// <summary>The model leaf of <c>AgentTask</c> — a narrow projection so the task envelope's heavy fields (workspace / permissions / prompt) are never materialized here.</summary>
    private sealed record ModelSlice(string? Model);

    private sealed record Row(Guid Id, AgentRunStatus Status, DateTimeOffset? StartedAt, DateTimeOffset? CompletedAt, string? ResultJson, string? TaskJson);

    private static readonly IReadOnlyDictionary<Guid, AgentRunMetrics> Empty = new Dictionary<Guid, AgentRunMetrics>();
}
