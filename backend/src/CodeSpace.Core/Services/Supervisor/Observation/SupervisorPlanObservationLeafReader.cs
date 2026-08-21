using System.Data;
using System.Data.Common;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Supervisor.Observation.Exceptions;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Dtos.Workflows.Supervisor;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace CodeSpace.Core.Services.Supervisor.Observation;

/// <summary>
/// Exact-team/run raw-ADO reader for bounded Plan display leaves. SQL pages identities before touching JSONB and returns
/// only bounded id/title/model/token projections; full payload/outcome values never cross the DB boundary. PostgreSQL
/// still detoasts the source JSONB to evaluate those leaf expressions, so this is not a body-sidecar replacement.
/// Unique ASCII-casing variants preserve AgentJson's case-insensitive property reads; casing duplicates are Invalid
/// because JSONB canonicalization has already destroyed the original last-wins property order.
/// Internal foundation only: current Journal/Room/timeline consumers still use #1615's full observation bundle.
/// </summary>
public sealed class SupervisorPlanObservationLeafReader : ISupervisorPlanObservationLeafReader, IScopedDependency
{
    private const int TokenTextMaximumChars = 64;

    internal static readonly string TailSql = BuildSql(cursorPredicate: null, descending: true);
    internal static readonly string OlderSql = BuildSql("AND decision.story_order < @cursor", descending: true);
    internal static readonly string NewerSql = BuildSql("AND decision.story_order > @cursor", descending: false);

    private readonly CodeSpaceDbContext _db;

    public SupervisorPlanObservationLeafReader(CodeSpaceDbContext db) { _db = db; }

    public async Task<SupervisorPlanObservationPage?> ReadPageAsync(SupervisorPlanObservationPageRequest request, CancellationToken cancellationToken)
    {
        var cursor = Validate(request);
        return await InSnapshotAsync(async (connection, token) =>
        {
            if (!await IsOwnedAsync(connection, request.TeamId, request.SupervisorRunId, token).ConfigureAwait(false)) return null;

            var heads = await ReadHeadsAsync(connection, request.TeamId, request.SupervisorRunId, token).ConfigureAwait(false);
            var changeFeedWatermark = cursor?.SnapshotRevision ?? heads.ObservationRevision;
            var rows = await ReadRowsAsync(connection, PageSql(request.Mode), request, cursor?.StoryOrder ?? 0, token).ConfigureAwait(false);
            var hasMore = rows.Count > request.Limit;
            if (hasMore) rows.RemoveAt(rows.Count - 1);
            if (request.Mode != SupervisorDecisionObservationStoryPageMode.Newer) rows.Reverse();

            var nextNewerOrder = rows.Count > 0 ? rows[^1].Metadata.StoryOrder : cursor?.StoryOrder ?? heads.StoryOrder;
            return new SupervisorPlanObservationPage
            {
                SupervisorRunId = request.SupervisorRunId,
                Mode = request.Mode.ToString(),
                RequestCursor = request.Cursor,
                Limit = request.Limit,
                SnapshotRevision = changeFeedWatermark,
                HeadRevision = heads.ObservationRevision,
                Items = rows,
                HasMore = hasMore,
                NextOlderCursor = request.Mode != SupervisorDecisionObservationStoryPageMode.Newer && hasMore
                    ? new SupervisorDecisionObservationStoryCursor(request.TeamId, request.SupervisorRunId, rows[0].Metadata.StoryOrder, changeFeedWatermark).Encode()
                    : null,
                NextNewerCursor = new SupervisorDecisionObservationStoryCursor(request.TeamId, request.SupervisorRunId, nextNewerOrder, changeFeedWatermark).Encode(),
            };
        }, cancellationToken).ConfigureAwait(false);
    }

    private async Task<T> InSnapshotAsync<T>(Func<DbConnection, CancellationToken, Task<T>> read, CancellationToken cancellationToken)
    {
        var connection = _db.Database.GetDbConnection();
        var closeAfter = connection.State != ConnectionState.Open;
        if (closeAfter) await _db.Database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        IDbContextTransaction? transaction = null;
        try
        {
            if (_db.Database.CurrentTransaction is null)
                transaction = await _db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, cancellationToken).ConfigureAwait(false);

            var result = await read(connection, cancellationToken).ConfigureAwait(false);
            if (transaction is not null) await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return result;
        }
        finally
        {
            if (transaction is not null) await transaction.DisposeAsync().ConfigureAwait(false);
            if (closeAfter) await _db.Database.CloseConnectionAsync().ConfigureAwait(false);
        }
    }

    private async Task<bool> IsOwnedAsync(DbConnection connection, Guid teamId, Guid supervisorRunId, CancellationToken cancellationToken)
    {
        await using var command = Command(connection, SupervisorDecisionObservationMetadataReader.OwnershipSql);
        AddScope(command, teamId, supervisorRunId);
        return (bool)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
    }

    private async Task<ObservationHeads> ReadHeadsAsync(DbConnection connection, Guid teamId, Guid supervisorRunId, CancellationToken cancellationToken)
    {
        await using var command = Command(connection, SupervisorDecisionObservationMetadataReader.HeadsSql);
        AddScope(command, teamId, supervisorRunId);
        await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SingleRow, cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) throw new InvalidOperationException("Supervisor observation heads query returned no row.");
        return new ObservationHeads(reader.GetInt64(0), reader.GetInt64(1));
    }

    private async Task<List<SupervisorPlanObservationItem>> ReadRowsAsync(DbConnection connection, string sql, SupervisorPlanObservationPageRequest request, long cursor, CancellationToken cancellationToken)
    {
        var take = checked(request.Limit + 1);
        await using var command = Command(connection, sql);
        AddScope(command, request.TeamId, request.SupervisorRunId);
        Add(command, "plan_kind", DbType.String, SupervisorDecisionKinds.Plan);
        Add(command, "cursor", DbType.Int64, cursor);
        Add(command, "take", DbType.Int32, take);
        Add(command, "error_chars", DbType.Int32, SupervisorDecisionObservationMetadataReader.ErrorPrefixMaximumChars);
        Add(command, "max_subtasks", DbType.Int32, SupervisorPlanObservationLeafLimits.MaximumSubtasks);
        Add(command, "id_chars", DbType.Int32, SupervisorPlanObservationLeafLimits.MaximumIdChars);
        Add(command, "title_chars", DbType.Int32, SupervisorPlanObservationLeafLimits.MaximumTitleChars);
        Add(command, "model_chars", DbType.Int32, SupervisorPlanObservationLeafLimits.MaximumModelChars);
        Add(command, "token_chars", DbType.Int32, TokenTextMaximumChars);

        var rows = new List<SupervisorPlanObservationItem>(take);
        await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess, cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) rows.Add(SupervisorPlanObservationLeafWire.Read(reader));
        return rows;
    }

    private DbCommand Command(DbConnection connection, string sql)
    {
        var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Transaction = _db.Database.CurrentTransaction?.GetDbTransaction();
        return command;
    }

    private static void AddScope(DbCommand command, Guid teamId, Guid supervisorRunId)
    {
        Add(command, "team_id", DbType.Guid, teamId);
        Add(command, "run_id", DbType.Guid, supervisorRunId);
    }

    private static void Add(DbCommand command, string name, DbType type, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.DbType = type;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }

    private static string PageSql(SupervisorDecisionObservationStoryPageMode mode) => mode switch
    {
        SupervisorDecisionObservationStoryPageMode.Tail => TailSql,
        SupervisorDecisionObservationStoryPageMode.Older => OlderSql,
        SupervisorDecisionObservationStoryPageMode.Newer => NewerSql,
        _ => throw new UnreachableException(),
    };

    private static SupervisorDecisionObservationStoryCursor? Validate(SupervisorPlanObservationPageRequest request)
    {
        request.ValidateShape();
        if (request.Cursor is null) return null;
        if (!SupervisorDecisionObservationStoryCursor.TryDecode(request.Cursor, request.TeamId, request.SupervisorRunId, out var cursor))
            throw new SupervisorDecisionObservationReadRequestException(["Cursor must be an opaque v1 story cursor for this exact team and SupervisorRun."]);
        if (request.Mode == SupervisorDecisionObservationStoryPageMode.Older && cursor.StoryOrder == 0)
            throw new SupervisorDecisionObservationReadRequestException(["Older requires a positive story cursor."]);
        return cursor;
    }

    private static string BuildSql(string? cursorPredicate, bool descending)
    {
        var direction = descending ? "DESC" : "ASC";
        return $$"""
            WITH page_ids AS MATERIALIZED (
                SELECT decision.id, decision.story_order, decision.ctid AS row_locator
                FROM supervisor_decision AS decision
                WHERE decision.team_id = @team_id
                  AND decision.supervisor_run_id = @run_id
                  AND decision.decision_kind = @plan_kind
                  {{cursorPredicate}}
                ORDER BY decision.story_order {{direction}}
                LIMIT @take
            )
            SELECT projected.*
            FROM page_ids
            JOIN LATERAL (
              SELECT
                decision.id, decision.supervisor_run_id, decision.decision_kind, decision.status,
                decision.story_order, decision.observation_revision, decision.created_date, decision.last_modified_date,
                LEFT(decision.error, @error_chars) AS error_prefix,
                COALESCE(OCTET_LENGTH(decision.error), 0) AS error_total_bytes,
                COALESCE(jsonb_typeof(decision.payload_jsonb), 'missing') AS payload_root_kind,
                plan_subtasks.matching_count > 0 AS subtasks_present,
                CASE WHEN plan_subtasks.matching_count > 1 THEN 'duplicate' ELSE jsonb_typeof(plan_subtasks.value) END AS subtasks_kind,
                CASE WHEN jsonb_typeof(plan_subtasks.value) = 'array' THEN jsonb_array_length(plan_subtasks.value) ELSE 0 END AS subtasks_total_count,
                subtask_leaves.leaf_json,
                subtask_leaves.invalid_count,
                subtask_leaves.truncated_count,
                COALESCE(jsonb_typeof(decision.outcome_jsonb), 'missing') AS outcome_root_kind,
                plan_usage.value IS NOT NULL AS usage_present,
                jsonb_typeof(plan_usage.value) AS usage_kind,
                jsonb_typeof(usage_model.value) AS model_kind,
                CASE WHEN jsonb_typeof(usage_model.value) = 'string' THEN LEFT(usage_model.value #>> '{}', @model_chars) END AS model_prefix,
                CASE WHEN jsonb_typeof(usage_model.value) = 'string' THEN OCTET_LENGTH(usage_model.value #>> '{}') END AS model_total_bytes,
                CASE WHEN jsonb_typeof(usage_model.value) = 'string' THEN CHAR_LENGTH(usage_model.value #>> '{}') END AS model_total_chars,
                jsonb_typeof(usage_input.value) AS input_kind,
                CASE WHEN usage_input.value IS NOT NULL THEN LEFT(usage_input.value::text, @token_chars) END AS input_text,
                CASE WHEN usage_input.value IS NOT NULL THEN CHAR_LENGTH(usage_input.value::text) END AS input_total_chars,
                jsonb_typeof(usage_output.value) AS output_kind,
                CASE WHEN usage_output.value IS NOT NULL THEN LEFT(usage_output.value::text, @token_chars) END AS output_text,
                CASE WHEN usage_output.value IS NOT NULL THEN CHAR_LENGTH(usage_output.value::text) END AS output_total_chars
              FROM supervisor_decision AS decision
            LEFT JOIN LATERAL (
                SELECT matches.matching_count,
                    CASE WHEN matches.matching_count = 1 THEN (
                        SELECT property.value
                        FROM jsonb_each(CASE WHEN jsonb_typeof(decision.payload_jsonb) = 'object' THEN decision.payload_jsonb ELSE '{}'::jsonb END)
                            AS property(key, value)
                        WHERE LOWER(property.key COLLATE "C") = 'subtasks'
                        LIMIT 1
                    ) END AS value
                FROM (
                    SELECT COALESCE(SUM(1), 0)::int AS matching_count
                    FROM jsonb_object_keys(CASE WHEN jsonb_typeof(decision.payload_jsonb) = 'object' THEN decision.payload_jsonb ELSE '{}'::jsonb END)
                        AS property(key)
                    WHERE LOWER(property.key COLLATE "C") = 'subtasks'
                ) AS matches
            ) AS plan_subtasks ON TRUE
            LEFT JOIN LATERAL (
                SELECT
                    COALESCE(jsonb_agg(jsonb_build_object(
                        'idPrefix', subtask_id.prefix,
                        'idTotalBytes', COALESCE(subtask_id.total_bytes, 0),
                        'titlePrefix', subtask_title.prefix,
                        'titleTotalBytes', COALESCE(subtask_title.total_bytes, 0)
                    ) ORDER BY element.ordinal), '[]'::jsonb)::text AS leaf_json,
                    COALESCE(SUM(CASE WHEN jsonb_typeof(element.value) <> 'object'
                        OR subtask_id.matching_count <> 1
                        OR subtask_title.matching_count <> 1
                        OR subtask_instruction.matching_count <> 1
                        OR COALESCE(subtask_id.value_kind, 'missing') <> 'string'
                        OR COALESCE(subtask_title.value_kind, 'missing') <> 'string'
                        OR COALESCE(subtask_instruction.value_kind, 'missing') <> 'string'
                        THEN 1 ELSE 0 END), 0)::bigint AS invalid_count,
                    COALESCE(SUM(CASE WHEN (subtask_id.value_kind = 'string' AND subtask_id.total_chars > @id_chars)
                        OR (subtask_title.value_kind = 'string' AND subtask_title.total_chars > @title_chars)
                        THEN 1 ELSE 0 END), 0)::bigint AS truncated_count
                FROM (
                    SELECT item.value, item.ordinal
                    FROM jsonb_array_elements(CASE WHEN jsonb_typeof(plan_subtasks.value) = 'array' THEN plan_subtasks.value ELSE '[]'::jsonb END)
                        WITH ORDINALITY AS item(value, ordinal)
                    ORDER BY item.ordinal
                    LIMIT @max_subtasks
                ) AS element
                LEFT JOIN LATERAL (
                    SELECT
                        COALESCE(SUM(1), 0)::int AS matching_count,
                        MIN(jsonb_typeof(property.value)) AS value_kind,
                        MIN(CASE WHEN jsonb_typeof(property.value) = 'string' THEN LEFT(property.value #>> '{}', @id_chars) END) AS prefix,
                        MIN(CASE WHEN jsonb_typeof(property.value) = 'string' THEN OCTET_LENGTH(property.value #>> '{}') END) AS total_bytes,
                        MIN(CASE WHEN jsonb_typeof(property.value) = 'string' THEN CHAR_LENGTH(property.value #>> '{}') END) AS total_chars
                    FROM jsonb_each(CASE WHEN jsonb_typeof(element.value) = 'object' THEN element.value ELSE '{}'::jsonb END)
                        WITH ORDINALITY AS property(key, value, ordinal)
                    WHERE LOWER(property.key COLLATE "C") = 'id'
                ) AS subtask_id ON TRUE
                LEFT JOIN LATERAL (
                    SELECT
                        COALESCE(SUM(1), 0)::int AS matching_count,
                        MIN(jsonb_typeof(property.value)) AS value_kind,
                        MIN(CASE WHEN jsonb_typeof(property.value) = 'string' THEN LEFT(property.value #>> '{}', @title_chars) END) AS prefix,
                        MIN(CASE WHEN jsonb_typeof(property.value) = 'string' THEN OCTET_LENGTH(property.value #>> '{}') END) AS total_bytes,
                        MIN(CASE WHEN jsonb_typeof(property.value) = 'string' THEN CHAR_LENGTH(property.value #>> '{}') END) AS total_chars
                    FROM jsonb_each(CASE WHEN jsonb_typeof(element.value) = 'object' THEN element.value ELSE '{}'::jsonb END)
                        WITH ORDINALITY AS property(key, value, ordinal)
                    WHERE LOWER(property.key COLLATE "C") = 'title'
                ) AS subtask_title ON TRUE
                LEFT JOIN LATERAL (
                    SELECT
                        COALESCE(SUM(1), 0)::int AS matching_count,
                        MIN(jsonb_typeof(property.value)) AS value_kind
                    FROM jsonb_each(CASE WHEN jsonb_typeof(element.value) = 'object' THEN element.value ELSE '{}'::jsonb END)
                        WITH ORDINALITY AS property(key, value, ordinal)
                    WHERE LOWER(property.key COLLATE "C") = 'instruction'
                ) AS subtask_instruction ON TRUE
            ) AS subtask_leaves ON TRUE
            LEFT JOIN LATERAL (
                SELECT decision.outcome_jsonb -> 'modelUsage' AS value
                WHERE jsonb_typeof(decision.outcome_jsonb) = 'object'
                  AND decision.outcome_jsonb ? 'modelUsage'
            ) AS plan_usage ON TRUE
            LEFT JOIN LATERAL (SELECT plan_usage.value -> 'model' AS value) AS usage_model ON TRUE
            LEFT JOIN LATERAL (SELECT plan_usage.value -> 'inputTokens' AS value) AS usage_input ON TRUE
            LEFT JOIN LATERAL (SELECT plan_usage.value -> 'outputTokens' AS value) AS usage_output ON TRUE
              WHERE decision.team_id = @team_id
                AND decision.supervisor_run_id = @run_id
                AND decision.story_order = page_ids.story_order
                AND decision.id = page_ids.id
                AND decision.ctid = page_ids.row_locator
              LIMIT 1
            ) AS projected ON TRUE
            ORDER BY projected.story_order {{direction}}
            """;
    }

    private readonly record struct ObservationHeads(long StoryOrder, long ObservationRevision);
}

internal static class SupervisorPlanObservationLeafWire
{
    internal static SupervisorPlanObservationItem Read(DbDataReader reader)
    {
        var metadata = SupervisorDecisionObservationWire.Read(reader);
        var payloadRootKind = reader.GetString(10);
        var subtasksPresent = reader.GetBoolean(11);
        var subtasksKind = NullableString(reader, 12);
        var totalCount = reader.GetInt32(13);
        var leafJson = reader.GetString(14);
        var invalidCount = reader.GetInt64(15);
        var truncatedCount = reader.GetInt64(16);
        var outcomeRootKind = reader.GetString(17);
        var usagePresent = reader.GetBoolean(18);
        var usageKind = NullableString(reader, 19);
        var modelKind = NullableString(reader, 20);
        var modelPrefix = NullableString(reader, 21);
        var modelTotalBytes = NullableInt(reader, 22);
        var modelTotalChars = NullableInt(reader, 23);
        var inputTokens = ReadToken(NullableString(reader, 24), NullableString(reader, 25), NullableInt(reader, 26));
        var outputTokens = ReadToken(NullableString(reader, 27), NullableString(reader, 28), NullableInt(reader, 29));

        List<LeafWire>? wireLeaves;
        try { wireLeaves = JsonSerializer.Deserialize<List<LeafWire>>(leafJson, AgentJson.Options); }
        catch (JsonException) { wireLeaves = null; }

        var subtaskState = DecodeSubtasks(new SubtaskStateInput(payloadRootKind, subtasksPresent, subtasksKind, totalCount, invalidCount, truncatedCount), wireLeaves);
        var subtasks = TrustedSubtasks(subtaskState, wireLeaves);
        var modelState = DecodeModelUsage(new ModelUsageStateInput(outcomeRootKind, usagePresent, usageKind, modelKind, modelPrefix, modelTotalBytes, modelTotalChars));
        var modelUsage = modelState is SupervisorPlanObservationLeafState.Exact or SupervisorPlanObservationLeafState.Truncated
            ? new SupervisorPlanModelUsageObservationLeaf
            {
                ModelPrefix = modelPrefix!,
                ModelTotalBytes = modelTotalBytes!.Value,
                InputTokens = inputTokens,
                OutputTokens = outputTokens,
            }
            : null;

        return new SupervisorPlanObservationItem
        {
            Metadata = metadata,
            SubtasksState = subtaskState,
            SubtasksTotalCount = Math.Max(totalCount, 0),
            SubtasksOmittedCount = subtaskState switch
            {
                SupervisorPlanObservationLeafState.Exact => 0,
                SupervisorPlanObservationLeafState.Truncated => Math.Max(totalCount - subtasks.Count, 0),
                _ => Math.Max(totalCount, 0),
            },
            Subtasks = subtasks,
            ModelUsageState = modelState,
            ModelUsage = modelUsage,
        };
    }

    private static SupervisorPlanObservationLeafState DecodeSubtasks(SubtaskStateInput input, IReadOnlyList<LeafWire>? leaves)
    {
        if (input.PayloadRootKind != "object") return SupervisorPlanObservationLeafState.Invalid;
        if (!input.Present || input.Kind == "null") return SupervisorPlanObservationLeafState.Missing;
        if (input.Kind != "array") return SupervisorPlanObservationLeafState.Invalid;
        if (input.Total < 0 || input.Invalid < 0 || input.Truncated < 0 || leaves is null) return SupervisorPlanObservationLeafState.Corrupt;
        if (leaves.Count != Math.Min(input.Total, SupervisorPlanObservationLeafLimits.MaximumSubtasks)) return SupervisorPlanObservationLeafState.Corrupt;
        if (input.Invalid > 0 || leaves.Any(leaf => leaf.IdPrefix is null || leaf.TitlePrefix is null || leaf.IdTotalBytes < 0 || leaf.TitleTotalBytes < 0))
            return SupervisorPlanObservationLeafState.Invalid;
        if (input.Total > SupervisorPlanObservationLeafLimits.MaximumSubtasks || input.Truncated > 0) return SupervisorPlanObservationLeafState.Truncated;
        return SupervisorPlanObservationLeafState.Exact;
    }

    private static IReadOnlyList<SupervisorPlanSubtaskObservationLeaf> TrustedSubtasks(SupervisorPlanObservationLeafState state, IReadOnlyList<LeafWire>? leaves)
    {
        if (state is not (SupervisorPlanObservationLeafState.Exact or SupervisorPlanObservationLeafState.Truncated) || leaves is null) return [];
        return leaves.Select(leaf => new SupervisorPlanSubtaskObservationLeaf
        {
            IdPrefix = leaf.IdPrefix!,
            IdTotalBytes = leaf.IdTotalBytes,
            TitlePrefix = leaf.TitlePrefix!,
            TitleTotalBytes = leaf.TitleTotalBytes,
        }).ToList();
    }

    private static SupervisorPlanObservationLeafState DecodeModelUsage(ModelUsageStateInput input)
    {
        if (input.OutcomeRootKind == "missing") return SupervisorPlanObservationLeafState.Missing;
        if (input.OutcomeRootKind != "object") return SupervisorPlanObservationLeafState.Invalid;
        if (!input.Present) return SupervisorPlanObservationLeafState.Missing;
        if (input.UsageKind != "object" || input.ModelKind != "string" || string.IsNullOrWhiteSpace(input.ModelPrefix)) return SupervisorPlanObservationLeafState.Invalid;
        if (input.ModelTotalBytes is null or < 0 || input.ModelTotalChars is null or < 0) return SupervisorPlanObservationLeafState.Corrupt;
        if (Encoding.UTF8.GetByteCount(input.ModelPrefix) > input.ModelTotalBytes) return SupervisorPlanObservationLeafState.Corrupt;
        return input.ModelTotalChars > SupervisorPlanObservationLeafLimits.MaximumModelChars
            ? SupervisorPlanObservationLeafState.Truncated
            : SupervisorPlanObservationLeafState.Exact;
    }

    private static int? ReadToken(string? kind, string? text, int? totalChars)
    {
        if (kind != "number" || text is null || totalChars is null or < 1 || totalChars > 64 || text.Length != totalChars) return null;
        try
        {
            using var document = JsonDocument.Parse(text);
            return document.RootElement.TryGetInt32(out var value) ? value : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? NullableString(DbDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    private static int? NullableInt(DbDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetInt32(ordinal);

    private sealed record SubtaskStateInput(string PayloadRootKind, bool Present, string? Kind, int Total, long Invalid, long Truncated);

    private sealed record ModelUsageStateInput(string OutcomeRootKind, bool Present, string? UsageKind, string? ModelKind, string? ModelPrefix, int? ModelTotalBytes, int? ModelTotalChars);

    private sealed record LeafWire
    {
        public string? IdPrefix { get; init; }
        public int IdTotalBytes { get; init; }
        public string? TitlePrefix { get; init; }
        public int TitleTotalBytes { get; init; }
    }
}
