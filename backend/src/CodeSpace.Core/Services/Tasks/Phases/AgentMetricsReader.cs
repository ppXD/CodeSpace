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
/// TWO logical batch reads — the <c>AgentRun</c> observations + grouped harness-native tool counts. The ONE place that turns the
/// durable agent record into <see cref="AgentRunMetrics"/>, so a plain <c>agent.run</c> / map agent surfaces the SAME
/// rollup <c>SupervisorPhaseSource</c> folds from its decision ledger. Duration is LIVE (recomputed at <c>now</c>);
/// Workflow Run Room/Journal path uses an exact team+run SQL projection whose bounded leaves never materialize the whole
/// result/task graph. The retained standalone <see cref="ReadAsync"/> path preserves its legacy full-envelope behavior.
/// A malformed/partial observation reads as unknown, never fabricates a figure. READ-ONLY.
/// </summary>
public sealed class AgentMetricsReader : IScopedDependency
{
    private readonly CodeSpaceDbContext _db;

    public AgentMetricsReader(CodeSpaceDbContext db)
    {
        _db = db;
    }

    /// <summary>Legacy standalone-agent read. Workflow-owned UI callers must use <see cref="ReadForWorkflowRunAsync"/> so id-list trust and full JSON materialization cannot enter Room/Journal.</summary>
    public async Task<IReadOnlyDictionary<Guid, AgentRunMetrics>> ReadAsync(Guid teamId, IReadOnlyCollection<Guid> agentRunIds, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (agentRunIds.Count == 0) return Empty;

        var ids = agentRunIds as IReadOnlyList<Guid> ?? agentRunIds.ToList();

        var rows = await _db.AgentRun.AsNoTracking()
            .Where(r => r.TeamId == teamId && ids.Contains(r.Id))
            .Select(r => new Row(r.Id, r.Status, r.StartedAt, r.CompletedAt, r.ResultJson, r.TaskJson, r.Error))
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        var toolCounts = await ToolCountsByAgentAsync(_db, teamId, ids, cancellationToken).ConfigureAwait(false);

        return rows.ToDictionary(r => r.Id, r => Build(r.Id, r.Status, r.StartedAt, r.CompletedAt, r.ResultJson, r.TaskJson, r.Error, toolCounts.GetValueOrDefault(r.Id), now));
    }

    /// <summary>
    /// The Workflow Run observation path used by Room/Journal. Unlike <see cref="ReadAsync"/> (kept for standalone
    /// agent-run callers), every row is bound to the exact team + Workflow Run and PostgreSQL returns only bounded card
    /// leaves — never the durable task/result carriers. Large maps are partitioned into fixed-size query batches without
    /// de-selecting agents. Database faults surface; no partial observation is returned as a successful read.
    /// </summary>
    public async Task<IReadOnlyDictionary<Guid, AgentRunMetrics>> ReadForWorkflowRunAsync(Guid teamId, Guid workflowRunId, IReadOnlyCollection<Guid> agentRunIds, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (agentRunIds.Count == 0) return Empty;

        var result = new Dictionary<Guid, AgentRunMetrics>();

        foreach (var ids in agentRunIds.Distinct().Chunk(MaxAgentIdsPerBatch))
        {
            var rows = await WorkflowRowsQuery(_db, teamId, workflowRunId, ids).ToListAsync(cancellationToken).ConfigureAwait(false);
            var toolCounts = await WorkflowToolCountsQuery(_db, teamId, workflowRunId, ids).ToDictionaryAsync(x => x.AgentRunId, x => x.Count, cancellationToken).ConfigureAwait(false);

            foreach (var row in rows)
            {
                if (!Enum.TryParse<AgentRunStatus>(row.Status, ignoreCase: false, out var status)) continue;   // future/corrupt persisted status → no fabricated card
                result[row.Id] = Build(row, status, toolCounts.GetValueOrDefault(row.Id), now);
            }
        }

        return result;
    }

    /// <summary>
    /// Exact workflow-scoped, bounded observation SQL. Canonical camelCase plus the PascalCase legacy form accepted by
    /// <see cref="AgentJson.Options"/> are recognized independently per leaf; a wrong-shaped leaf is unknown without
    /// poisoning healthy siblings. Result/task roots remain durable in PostgreSQL and never cross into CLR memory.
    /// Internal so tests pin the translated SQL boundary itself.
    /// </summary>
    internal static IQueryable<WorkflowAgentMetricsRow> WorkflowRowsQuery(CodeSpaceDbContext db, Guid teamId, Guid workflowRunId, Guid[] agentRunIds) =>
        db.Database.SqlQuery<WorkflowAgentMetricsRow>($$"""
            SELECT a.id AS id,
                   a.status::text AS status,
                   a.started_at AS started_at,
                   a.completed_at AS completed_at,
                   CASE WHEN char_length(leaves.result_model) <= 512 THEN leaves.result_model END AS result_model,
                   CASE WHEN char_length(leaves.task_model) <= 512 THEN leaves.task_model END AS task_model,
                   CASE WHEN char_length(leaves.harness) <= 512 THEN leaves.harness END AS harness,
                   leaves.resumed AS resumed,
                   tokens.input_tokens AS input_tokens,
                   tokens.output_tokens AS output_tokens,
                   files.changed_file_count AS changed_file_count,
                   files.changed_files_json AS changed_files_json,
                   stats.file_stats_json AS file_stats_json,
                   left(title.line, 140) AS goal_prefix,
                   COALESCE(char_length(title.line) > 140, FALSE) AS goal_truncated,
                   left(error_text.normalized, 400) AS error_prefix,
                   COALESCE(char_length(error_text.normalized) > 400, FALSE) AS error_truncated
            FROM agent_run AS a
            LEFT JOIN LATERAL (
                SELECT COALESCE(a.result_jsonb -> 'tokenUsage', a.result_jsonb -> 'TokenUsage') AS token_usage,
                       COALESCE(a.result_jsonb -> 'changedFiles', a.result_jsonb -> 'ChangedFiles') AS changed_files,
                       COALESCE(a.result_jsonb -> 'fileStats', a.result_jsonb -> 'FileStats') AS file_stats,
                       CASE WHEN jsonb_typeof(COALESCE(a.result_jsonb -> 'model', a.result_jsonb -> 'Model')) = 'string' THEN COALESCE(a.result_jsonb ->> 'model', a.result_jsonb ->> 'Model') END AS result_model,
                       CASE WHEN jsonb_typeof(COALESCE(a.result_jsonb -> 'error', a.result_jsonb -> 'Error')) = 'string' THEN COALESCE(a.result_jsonb ->> 'error', a.result_jsonb ->> 'Error') END AS result_error,
                       CASE WHEN jsonb_typeof(COALESCE(a.task_jsonb -> 'model', a.task_jsonb -> 'Model')) = 'string' THEN COALESCE(a.task_jsonb ->> 'model', a.task_jsonb ->> 'Model') END AS task_model,
                       CASE WHEN jsonb_typeof(COALESCE(a.task_jsonb -> 'harness', a.task_jsonb -> 'Harness')) = 'string' THEN COALESCE(a.task_jsonb ->> 'harness', a.task_jsonb ->> 'Harness') END AS harness,
                       COALESCE(a.task_jsonb -> 'displayTitle', a.task_jsonb -> 'DisplayTitle') AS display_title_json,
                       COALESCE(a.task_jsonb -> 'goal', a.task_jsonb -> 'Goal') AS goal_json,
                       CASE WHEN jsonb_typeof(COALESCE(a.task_jsonb -> 'resumeFromSessionId', a.task_jsonb -> 'ResumeFromSessionId')) = 'string'
                           THEN nullif(regexp_replace(COALESCE(a.task_jsonb ->> 'resumeFromSessionId', a.task_jsonb ->> 'ResumeFromSessionId'), '^[[:space:]]+|[[:space:]]+$', '', 'g'), '') IS NOT NULL
                           ELSE FALSE END AS resumed
            ) AS leaves ON TRUE
            LEFT JOIN LATERAL (
                SELECT CASE WHEN jsonb_typeof(COALESCE(leaves.token_usage -> 'inputTokens', leaves.token_usage -> 'InputTokens')) = 'number'
                                  AND COALESCE(leaves.token_usage ->> 'inputTokens', leaves.token_usage ->> 'InputTokens') ~ '^-?[0-9]+$'
                                  AND (COALESCE(leaves.token_usage ->> 'inputTokens', leaves.token_usage ->> 'InputTokens'))::numeric BETWEEN -2147483648 AND 2147483647
                                THEN (COALESCE(leaves.token_usage ->> 'inputTokens', leaves.token_usage ->> 'InputTokens'))::integer END AS input_tokens,
                       CASE WHEN jsonb_typeof(COALESCE(leaves.token_usage -> 'outputTokens', leaves.token_usage -> 'OutputTokens')) = 'number'
                                  AND COALESCE(leaves.token_usage ->> 'outputTokens', leaves.token_usage ->> 'OutputTokens') ~ '^-?[0-9]+$'
                                  AND (COALESCE(leaves.token_usage ->> 'outputTokens', leaves.token_usage ->> 'OutputTokens'))::numeric BETWEEN -2147483648 AND 2147483647
                                THEN (COALESCE(leaves.token_usage ->> 'outputTokens', leaves.token_usage ->> 'OutputTokens'))::integer END AS output_tokens
            ) AS tokens ON TRUE
            LEFT JOIN LATERAL (
                SELECT CASE WHEN valid THEN jsonb_array_length(leaves.changed_files) END AS changed_file_count,
                       CASE WHEN valid THEN COALESCE((
                           SELECT jsonb_agg(item.value ORDER BY item.ordinality)::text
                           FROM (
                               SELECT entry.value, entry.ordinality
                               FROM jsonb_array_elements(leaves.changed_files) WITH ORDINALITY AS entry(value, ordinality)
                               ORDER BY entry.ordinality
                               LIMIT 40
                           ) AS item
                       ), '[]') END AS changed_files_json
                FROM (SELECT jsonb_typeof(leaves.changed_files) = 'array'
                    AND NOT EXISTS (
                        SELECT 1 FROM jsonb_array_elements(leaves.changed_files) AS entry(value)
                        WHERE jsonb_typeof(entry.value) <> 'string' OR octet_length(entry.value #>> '{}') > 4096
                    ) AS valid) AS validation
            ) AS files ON TRUE
            LEFT JOIN LATERAL (
                SELECT CASE WHEN valid THEN COALESCE((
                    SELECT jsonb_agg(jsonb_build_object(
                        'path', COALESCE(item.value ->> 'path', item.value ->> 'Path'),
                        'additions', CASE WHEN jsonb_typeof(COALESCE(item.value -> 'additions', item.value -> 'Additions')) = 'number'
                            AND COALESCE(item.value ->> 'additions', item.value ->> 'Additions') ~ '^-?[0-9]+$'
                            AND (COALESCE(item.value ->> 'additions', item.value ->> 'Additions'))::numeric BETWEEN -2147483648 AND 2147483647
                            THEN (COALESCE(item.value ->> 'additions', item.value ->> 'Additions'))::integer END,
                        'deletions', CASE WHEN jsonb_typeof(COALESCE(item.value -> 'deletions', item.value -> 'Deletions')) = 'number'
                            AND COALESCE(item.value ->> 'deletions', item.value ->> 'Deletions') ~ '^-?[0-9]+$'
                            AND (COALESCE(item.value ->> 'deletions', item.value ->> 'Deletions'))::numeric BETWEEN -2147483648 AND 2147483647
                            THEN (COALESCE(item.value ->> 'deletions', item.value ->> 'Deletions'))::integer END
                    ) ORDER BY item.ordinality)::text
                    FROM (
                        SELECT entry.value, entry.ordinality
                        FROM jsonb_array_elements(leaves.file_stats) WITH ORDINALITY AS entry(value, ordinality)
                        ORDER BY entry.ordinality
                        LIMIT 40
                    ) AS item
                ), '[]') ELSE '[]' END AS file_stats_json
                FROM (SELECT jsonb_typeof(leaves.file_stats) = 'array'
                    AND NOT EXISTS (
                        SELECT 1 FROM jsonb_array_elements(leaves.file_stats) AS entry(value)
                        WHERE jsonb_typeof(entry.value) <> 'object'
                           OR jsonb_typeof(COALESCE(entry.value -> 'path', entry.value -> 'Path')) <> 'string'
                           OR octet_length(COALESCE(entry.value ->> 'path', entry.value ->> 'Path')) > 4096
                           OR NOT (COALESCE(entry.value -> 'additions', entry.value -> 'Additions') IS NULL
                               OR jsonb_typeof(COALESCE(entry.value -> 'additions', entry.value -> 'Additions')) = 'null'
                               OR (jsonb_typeof(COALESCE(entry.value -> 'additions', entry.value -> 'Additions')) = 'number'
                                   AND COALESCE(entry.value ->> 'additions', entry.value ->> 'Additions') ~ '^-?[0-9]+$'
                                   AND (COALESCE(entry.value ->> 'additions', entry.value ->> 'Additions'))::numeric BETWEEN -2147483648 AND 2147483647))
                           OR NOT (COALESCE(entry.value -> 'deletions', entry.value -> 'Deletions') IS NULL
                               OR jsonb_typeof(COALESCE(entry.value -> 'deletions', entry.value -> 'Deletions')) = 'null'
                               OR (jsonb_typeof(COALESCE(entry.value -> 'deletions', entry.value -> 'Deletions')) = 'number'
                                   AND COALESCE(entry.value ->> 'deletions', entry.value ->> 'Deletions') ~ '^-?[0-9]+$'
                                   AND (COALESCE(entry.value ->> 'deletions', entry.value ->> 'Deletions'))::numeric BETWEEN -2147483648 AND 2147483647))
                    ) AS valid) AS validation
            ) AS stats ON TRUE
            LEFT JOIN LATERAL (
                SELECT CASE
                    WHEN jsonb_typeof(leaves.display_title_json) = 'string' THEN leaves.display_title_json #>> '{}'
                    WHEN (leaves.display_title_json IS NULL OR jsonb_typeof(leaves.display_title_json) = 'null') AND jsonb_typeof(leaves.goal_json) = 'string' THEN leaves.goal_json #>> '{}'
                END AS value
            ) AS title_source ON TRUE
            LEFT JOIN LATERAL (
                SELECT regexp_replace(line.value, '^[[:space:]]+|[[:space:]]+$', '', 'g') AS line
                FROM regexp_split_to_table(replace(replace(title_source.value, E'\r\n', E'\n'), E'\r', E'\n'), E'\n') WITH ORDINALITY AS line(value, ordinality)
                WHERE nullif(regexp_replace(line.value, '^[[:space:]]+|[[:space:]]+$', '', 'g'), '') IS NOT NULL
                ORDER BY line.ordinality
                LIMIT 1
            ) AS title ON TRUE
            LEFT JOIN LATERAL (
                SELECT CASE WHEN a.status <> 'Succeeded' THEN COALESCE(
                    nullif(regexp_replace(leaves.result_error, '^[[:space:]]+|[[:space:]]+$', '', 'g'), ''),
                    nullif(regexp_replace(a.error, '^[[:space:]]+|[[:space:]]+$', '', 'g'), '')) END AS value
            ) AS error_source ON TRUE
            LEFT JOIN LATERAL (
                SELECT regexp_replace(replace(replace(replace(error_source.value, E'\r\n', ' '), E'\r', ' '), E'\n', ' '), '^[[:space:]]+|[[:space:]]+$', '', 'g') AS normalized
            ) AS error_text ON TRUE
            WHERE a.team_id = {{teamId}}
              AND a.workflow_run_id = {{workflowRunId}}
              AND a.id = ANY ({{agentRunIds}})
            """);

    /// <summary>The harness-native tool count for the same exact workflow-scoped agent set; never trusts ids as tenancy.</summary>
    internal static IQueryable<WorkflowAgentToolCountRow> WorkflowToolCountsQuery(CodeSpaceDbContext db, Guid teamId, Guid workflowRunId, Guid[] agentRunIds) =>
        db.Database.SqlQuery<WorkflowAgentToolCountRow>($$"""
            SELECT e.agent_run_id AS agent_run_id, count(*)::integer AS count
            FROM agent_run_event AS e
            INNER JOIN agent_run AS a ON a.id = e.agent_run_id
            WHERE a.team_id = {{teamId}}
              AND a.workflow_run_id = {{workflowRunId}}
              AND a.id = ANY ({{agentRunIds}})
              AND e.kind = {{AgentEventKind.ToolCall.ToString()}}
            GROUP BY e.agent_run_id
            """);

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

    /// <summary>Turn one agent's persisted state into the metrics bundle — pure, so the live clock + the defensive parses + the cost pricing live in one unit-testable place. Tokens/files null until the result lands; model = what the run reported preferred over the spawn-pinned task model, null when neither carried one; cost null when the model is unpriced (fail-open).</summary>
    public static AgentRunMetrics Build(Guid id, AgentRunStatus status, DateTimeOffset? startedAt, DateTimeOffset? completedAt, string? resultJson, string? taskJson, string? rowError, int toolCount, DateTimeOffset now)
    {
        var result = TryDeserialize<ResultSlice>(resultJson);
        var task = TryDeserialize<TaskSlice>(taskJson);
        // Prefer the model the run ACTUALLY reported off its event stream over the spawn-PINNED task model, so an UNPINNED
        // run (common for Codex, which the operator often leaves blank) still shows what it used instead of a blank cell.
        var rawModel = !string.IsNullOrWhiteSpace(result?.Model) ? result!.Model : task?.Model;
        var model = string.IsNullOrWhiteSpace(rawModel) ? null : rawModel;

        var tokens = result?.TokenUsage;

        return new AgentRunMetrics
        {
            Status = status,
            Error = FailureError(status, result?.Error, rowError),
            Goal = DeriveTitle(task?.DisplayTitle ?? task?.Goal),
            DurationMs = ComputeDuration(startedAt, completedAt, now),
            InputTokens = tokens?.InputTokens,
            OutputTokens = tokens?.OutputTokens,
            ToolCount = toolCount,
            Model = model,
            CostUsd = tokens is null ? null : AgentCostPricing.CostUsd(model, tokens.InputTokens, tokens.OutputTokens),
            FilesChanged = result?.ChangedFiles?.Count,
            ChangedFiles = (result?.ChangedFiles ?? Array.Empty<string>()).Take(MaxChangedFiles).ToList(),
            ChangedFileStats = (result?.FileStats ?? Array.Empty<FileDiffStat>()).Take(MaxChangedFiles).ToList(),
            Resumed = !string.IsNullOrWhiteSpace(task?.ResumeFromSessionId),
            Harness = string.IsNullOrWhiteSpace(task?.Harness) ? null : task!.Harness,
        };
    }

    /// <summary>Fold one already-bounded workflow observation row. No durable JSON carrier exists on this type.</summary>
    private static AgentRunMetrics Build(WorkflowAgentMetricsRow row, AgentRunStatus status, int toolCount, DateTimeOffset now)
    {
        var model = !string.IsNullOrWhiteSpace(row.ResultModel) ? row.ResultModel : string.IsNullOrWhiteSpace(row.TaskModel) ? null : row.TaskModel;
        var tokensKnown = row.InputTokens is not null && row.OutputTokens is not null;

        return new AgentRunMetrics
        {
            Status = status,
            Error = CompleteBoundedText(row.ErrorPrefix, row.ErrorTruncated, MaxErrorChars),
            Goal = CompleteBoundedText(row.GoalPrefix, row.GoalTruncated, MaxGoalChars),
            DurationMs = ComputeDuration(row.StartedAt, row.CompletedAt, now),
            InputTokens = tokensKnown ? row.InputTokens : null,
            OutputTokens = tokensKnown ? row.OutputTokens : null,
            ToolCount = toolCount,
            Model = model,
            CostUsd = tokensKnown ? AgentCostPricing.CostUsd(model, row.InputTokens!.Value, row.OutputTokens!.Value) : null,
            FilesChanged = row.ChangedFileCount,
            ChangedFiles = DeserializeBounded<IReadOnlyList<string>>(row.ChangedFilesJson) ?? Array.Empty<string>(),
            ChangedFileStats = DeserializeBounded<IReadOnlyList<FileDiffStat>>(row.FileStatsJson) ?? Array.Empty<FileDiffStat>(),
            Resumed = row.Resumed,
            Harness = string.IsNullOrWhiteSpace(row.Harness) ? null : row.Harness,
        };
    }

    /// <summary>The SQL already limits Unicode code points; this final fold preserves the existing UTF-16 cap/ellipsis semantics, including astral characters.</summary>
    private static string? CompleteBoundedText(string? prefix, bool sourceWasLonger, int max)
    {
        if (prefix is null) return null;
        if (prefix.Length > max) return Truncate(prefix, max);
        return sourceWasLonger ? prefix.TrimEnd() + "…" : prefix;
    }

    /// <summary>Defensive parse of a SQL-produced bounded array only; malformed projection bytes fail closed per leaf.</summary>
    private static T? DeserializeBounded<T>(string? json) where T : class
    {
        if (json is null) return null;

        try { return JsonSerializer.Deserialize<T>(json, AgentJson.Options); }
        catch (JsonException) { return null; }
    }

    /// <summary>Bound on the per-agent changed-file list carried for the terminal's Files tab (the count is still the full total).</summary>
    private const int MaxChangedFiles = 40;
    private const int MaxAgentIdsPerBatch = 512;
    private const int MaxGoalChars = 140;

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

    /// <summary>The (already secret-redacted) failure reason for a non-succeeded agent: the RESULT's error (the harness's real cause — an LLM 4xx, a build failure) preferred over the ROW's error (set when the agent was cancelled / abandoned before it wrote a result). Null on a succeeded run (a green agent never shows a stray error) and when neither carries one. Bounded to a short single-line snippet so it never bloats the card.</summary>
    private static string? FailureError(AgentRunStatus status, string? resultError, string? rowError)
    {
        if (status == AgentRunStatus.Succeeded) return null;

        var raw = !string.IsNullOrWhiteSpace(resultError) ? resultError : rowError;

        return string.IsNullOrWhiteSpace(raw) ? null : Truncate(raw.ReplaceLineEndings(" ").Trim(), MaxErrorChars);
    }

    /// <summary>The card's error snippet cap — enough to read the cause (an LLM 4xx / a build error line) without carrying a full stack / litellm dump onto the poll payload.</summary>
    private const int MaxErrorChars = 400;

    /// <summary>Clamp to <paramref name="max"/> chars with an ellipsis, backing off a split surrogate pair (mirrors <c>AgentEventTimelineMap.Truncate</c>) so an astral char never leaves a lone surrogate.</summary>
    private static string Truncate(string text, int max)
    {
        if (text.Length <= max) return text;

        var cut = char.IsHighSurrogate(text[max - 1]) ? max - 1 : max;

        return string.Concat(text.AsSpan(0, cut).TrimEnd(), "…");
    }

    /// <summary>The leaves of <c>AgentRunResult</c> the metric needs — token usage + the model the run actually ran + the changed-file list (for its COUNT) + the per-file diffstat + the failure error — a narrow projection so the result blob's heavy fields (patch / summary / transcript) are never materialized on this poll-path.</summary>
    private sealed record ResultSlice(AgentTokenUsage? TokenUsage, string? Model, IReadOnlyList<string>? ChangedFiles, IReadOnlyList<FileDiffStat>? FileStats, string? Error);

    /// <summary>The display leaves of <c>AgentTask</c> — its model + goal + display title + resume marker + harness kind — a narrow projection so the task envelope's heavy fields (workspace / permissions / tools) are never materialized here.</summary>
    private sealed record TaskSlice(string? Model, string? Goal, string? DisplayTitle, string? ResumeFromSessionId, string? Harness);

    /// <summary>
    /// A concise one-line display TITLE from an agent's goal — so a fan-out branch reads as its subtask rather than a
    /// structural <c>map#N</c> key. Since B1 the goal is the CLEAN task (the persona rides its own SystemPrompt channel,
    /// not prepended), so the title is simply its FIRST non-empty line, trimmed and capped. A null/empty/whitespace
    /// goal → null (the row keeps its structural fallback).
    /// </summary>
    internal static string? DeriveTitle(string? goal)
    {
        if (string.IsNullOrWhiteSpace(goal)) return null;

        var normalized = goal.Replace("\r\n", "\n").Replace('\r', '\n');
        var line = normalized.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();

        if (string.IsNullOrEmpty(line)) return null;

        return line.Length <= MaxGoalChars ? line : line[..MaxGoalChars].TrimEnd() + "…";
    }

    private sealed record Row(Guid Id, AgentRunStatus Status, DateTimeOffset? StartedAt, DateTimeOffset? CompletedAt, string? ResultJson, string? TaskJson, string? Error);

    private static readonly IReadOnlyDictionary<Guid, AgentRunMetrics> Empty = new Dictionary<Guid, AgentRunMetrics>();
    private static readonly IReadOnlyDictionary<Guid, int> EmptyCounts = new Dictionary<Guid, int>();
}

/// <summary>Bounded Workflow Run observation row. Deliberately has no ResultJson/TaskJson property.</summary>
internal sealed class WorkflowAgentMetricsRow
{
    public Guid Id { get; set; }
    public string Status { get; set; } = default!;
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public string? ResultModel { get; set; }
    public string? TaskModel { get; set; }
    public string? Harness { get; set; }
    public bool Resumed { get; set; }
    public int? InputTokens { get; set; }
    public int? OutputTokens { get; set; }
    public int? ChangedFileCount { get; set; }
    public string? ChangedFilesJson { get; set; }
    public string? FileStatsJson { get; set; }
    public string? GoalPrefix { get; set; }
    public bool GoalTruncated { get; set; }
    public string? ErrorPrefix { get; set; }
    public bool ErrorTruncated { get; set; }
}

/// <summary>One bounded workflow-scoped harness-native tool count.</summary>
internal sealed class WorkflowAgentToolCountRow
{
    public Guid AgentRunId { get; set; }
    public int Count { get; set; }
}
