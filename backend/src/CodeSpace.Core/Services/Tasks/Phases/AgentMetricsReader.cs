using System.Text.Json;
using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Agents.Cost;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Decisions;
using CodeSpace.Messages.Enums;
using CodeSpace.Messages.Tasks.Phases;
using Microsoft.EntityFrameworkCore;

namespace CodeSpace.Core.Services.Tasks.Phases;

/// <summary>
/// Reads the per-agent metrics (status · duration · tokens · tool count · model) for a set of agent runs, team-scoped, in
/// TWO batch queries — the <c>AgentRun</c> rows + a grouped <c>tool_call_ledger</c> count. The ONE place that turns the
/// durable agent record into <see cref="AgentRunMetrics"/>, so a plain <c>agent.code</c> / map agent surfaces the SAME
/// rollup <c>SupervisorPhaseSource</c> folds from its decision ledger. Duration is LIVE (recomputed at <c>now</c>);
/// tokens/model/cost/files deserialize DEFENSIVELY from a NARROW projection of <c>ResultJson</c>/<c>TaskJson</c> — the
/// token-usage + changed-file list (for its COUNT) + model leaves, never the whole result/task graph (so the heavy
/// patch / transcript / summary / per-repo bodies aren't materialized on this poll-path) — and a malformed/partial blob
/// reads as unknown, never throws, so one bad row can't blank the whole projection. READ-ONLY.
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

        var toolCounts = await ToolCountsByAgentAsync(_db, teamId, ids, cancellationToken).ConfigureAwait(false);

        return rows.ToDictionary(r => r.Id, r => Build(r.Id, r.Status, r.StartedAt, r.CompletedAt, r.ResultJson, r.TaskJson, toolCounts.GetValueOrDefault(r.Id), now));
    }

    /// <summary>
    /// How many tool calls each agent ACTUALLY made — its harness-native tool invocations (Read / Edit / Bash / WebSearch
    /// …) off the append-only <c>agent_run_event</c> log (<see cref="AgentEventKind.ToolCall"/>), NOT the governed
    /// <c>tool_call_ledger</c> (which is empty unless a run routed side-effecting calls through the MCP governance fabric —
    /// so a plain Codex / Claude-Code run that used its own tools would otherwise read a misleading "0 tools"). The passed
    /// ids are already team-scoped by the caller (same trust the reasoning-count query relies on), so the log is keyed by
    /// agent run id. An agent with no tool events is absent → 0 downstream. The ONE place both phase sources count tools.
    /// </summary>
    public static async Task<IReadOnlyDictionary<Guid, int>> ToolCountsByAgentAsync(CodeSpaceDbContext db, Guid teamId, IReadOnlyList<Guid> agentRunIds, CancellationToken cancellationToken)
    {
        if (agentRunIds.Count == 0) return EmptyCounts;

        return await db.AgentRunEvent.AsNoTracking()
            .Where(e => agentRunIds.Contains(e.AgentRunId) && e.Kind == AgentEventKind.ToolCall)
            .GroupBy(e => e.AgentRunId)
            .Select(g => new { AgentRunId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.AgentRunId, x => x.Count, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Turn one agent's persisted state into the metrics bundle — pure, so the live clock + the defensive parses + the cost pricing live in one unit-testable place. Tokens/files null until the result lands; model null when the task left it blank; cost null when the model is unpriced (fail-open).</summary>
    public static AgentRunMetrics Build(Guid id, AgentRunStatus status, DateTimeOffset? startedAt, DateTimeOffset? completedAt, string? resultJson, string? taskJson, int toolCount, DateTimeOffset now)
    {
        var result = TryDeserialize<ResultSlice>(resultJson);
        var task = TryDeserialize<TaskSlice>(taskJson);
        var rawModel = task?.Model;
        var model = string.IsNullOrWhiteSpace(rawModel) ? null : rawModel;

        var tokens = result?.TokenUsage;

        return new AgentRunMetrics
        {
            Status = status,
            Goal = DeriveTitle(task?.Goal),
            DurationMs = ComputeDuration(startedAt, completedAt, now),
            InputTokens = tokens?.InputTokens,
            OutputTokens = tokens?.OutputTokens,
            ToolCount = toolCount,
            Model = model,
            CostUsd = tokens is null ? null : AgentCostPricing.CostUsd(model, tokens.InputTokens, tokens.OutputTokens),
            FilesChanged = result?.ChangedFiles?.Count,
        };
    }

    /// <summary>Live duration: final once terminal (<c>CompletedAt − StartedAt</c>), else elapsed (<c>now − StartedAt</c>); null before it starts; a negative span (clock skew) clamps to 0. The ONE place both phase sources compute an agent's run duration.</summary>
    public static long? ComputeDuration(DateTimeOffset? startedAt, DateTimeOffset? completedAt, DateTimeOffset now)
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

    /// <summary>The leaves of <c>AgentRunResult</c> the metric needs — token usage + the changed-file list (for its COUNT) — a narrow projection so the result blob's heavy fields (patch / summary / transcript) are never materialized on this poll-path.</summary>
    private sealed record ResultSlice(AgentTokenUsage? TokenUsage, IReadOnlyList<string>? ChangedFiles);

    /// <summary>The display leaves of <c>AgentTask</c> — its model + goal — a narrow projection so the task envelope's heavy fields (workspace / permissions / tools) are never materialized here.</summary>
    private sealed record TaskSlice(string? Model, string? Goal);

    /// <summary>
    /// A concise one-line display TITLE from an agent's goal — so a fan-out branch reads as its subtask rather than a
    /// structural <c>map#N</c> key. A persona-resolved goal is <c>"&lt;systemPrompt&gt;\n\n&lt;task&gt;"</c>
    /// (see <c>AgentDefinitionResolver.ComposeGoal</c>), so take the LAST blank-line block (the task half), its first
    /// non-empty line, trimmed and capped. A null/empty/whitespace goal → null (the row keeps its structural fallback).
    /// </summary>
    internal static string? DeriveTitle(string? goal)
    {
        if (string.IsNullOrWhiteSpace(goal)) return null;

        var normalized = goal.Replace("\r\n", "\n").Replace('\r', '\n');
        var block = normalized.Split("\n\n", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault() ?? normalized;
        var line = block.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();

        if (string.IsNullOrEmpty(line)) return null;

        const int max = 140;
        return line.Length <= max ? line : line[..max].TrimEnd() + "…";
    }

    private sealed record Row(Guid Id, AgentRunStatus Status, DateTimeOffset? StartedAt, DateTimeOffset? CompletedAt, string? ResultJson, string? TaskJson);

    private static readonly IReadOnlyDictionary<Guid, AgentRunMetrics> Empty = new Dictionary<Guid, AgentRunMetrics>();
    private static readonly IReadOnlyDictionary<Guid, int> EmptyCounts = new Dictionary<Guid, int>();
}
