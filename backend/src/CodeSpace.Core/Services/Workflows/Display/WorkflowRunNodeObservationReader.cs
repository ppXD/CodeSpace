using System.Data;
using System.Data.Common;
using System.Text.Json;
using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Dtos.Workflows;
using CodeSpace.Messages.Enums;
using Microsoft.EntityFrameworkCore;

namespace CodeSpace.Core.Services.Workflows.Display;

/// <summary>
/// Bounded structural-phase reader. The shared metadata plane supplies body-blind cells/links/topology; one batch
/// query then reads only each top-level cell's exact error prefix and the three flow.map output leaves. A state change
/// between those reads is detected and returned as unavailable, never combined into a torn phase.
/// </summary>
public sealed class WorkflowRunNodeObservationReader : IWorkflowRunNodeObservationReader, IScopedDependency
{
    public const int MaximumErrorCharacters = 2048;
    public const int MaximumNumberCharacters = 64;
    public const int MaximumCoverageBytes = 256 * 1024;
    public const int MaximumTotalCoverageBytes = 1024 * 1024;

    internal const string LeafSql = """
        WITH requested_run AS MATERIALIZED (
            SELECT id, source_type, root_run_id
            FROM workflow_run
            WHERE id = @requested_run_id AND team_id = @team_id
            LIMIT 1
        ), requested_cells AS MATERIALIZED (
            SELECT source_run_id, node_id, ordinal
            FROM unnest(@source_run_ids::uuid[], @node_ids::text[]) WITH ORDINALITY AS value(source_run_id, node_id, ordinal)
        ), resolved AS MATERIALIZED (
            SELECT cell.ordinal, cell.source_run_id, cell.node_id,
                   latest.record_type, latest.occurred_at, latest.payload_json
            FROM requested_cells AS cell
            CROSS JOIN requested_run AS requested
            INNER JOIN workflow_run AS source
              ON source.id = cell.source_run_id
             AND source.team_id = @team_id
             AND (source.id = requested.id OR (
                    @merge_lineage
                AND requested.source_type <> @child_source
                AND source.source_type <> @child_source
                AND coalesce(source.root_run_id, source.id) = coalesce(requested.root_run_id, requested.id)))
            INNER JOIN LATERAL (
                SELECT record.record_type, record.occurred_at, record.payload_json
                FROM workflow_run_record AS record
                WHERE record.run_id = cell.source_run_id
                  AND record.node_id = cell.node_id
                  AND record.iteration_key = ''
                  AND record.record_type LIKE 'node.%'
                ORDER BY record.sequence DESC
                LIMIT 1
            ) AS latest ON TRUE
        ), raw AS MATERIALIZED (
            SELECT resolved.*,
                   CASE WHEN resolved.record_type = 'node.failed' THEN resolved.payload_json -> 'error' END AS error_value,
                   CASE WHEN resolved.node_id = ANY(@map_node_ids::text[]) THEN resolved.payload_json -> 'outputs' END AS outputs_value
            FROM resolved
        ), assessed AS (
            SELECT raw.*,
                   jsonb_typeof(error_value) AS error_kind,
                   CASE WHEN jsonb_typeof(error_value) = 'string' THEN left(error_value #>> '{}', @error_chars) END AS error_prefix,
                   CASE WHEN jsonb_typeof(error_value) = 'string' THEN char_length(error_value #>> '{}') END AS error_total_chars,
                   jsonb_typeof(outputs_value) AS outputs_kind,
                   jsonb_typeof(outputs_value -> 'count') AS count_kind,
                   CASE WHEN outputs_value -> 'count' IS NOT NULL THEN left((outputs_value -> 'count')::text, @number_chars) END AS count_text,
                   CASE WHEN outputs_value -> 'count' IS NOT NULL THEN char_length((outputs_value -> 'count')::text) END AS count_total_chars,
                   jsonb_typeof(outputs_value -> 'failed') AS failed_kind,
                   CASE WHEN outputs_value -> 'failed' IS NOT NULL THEN left((outputs_value -> 'failed')::text, @number_chars) END AS failed_text,
                   CASE WHEN outputs_value -> 'failed' IS NOT NULL THEN char_length((outputs_value -> 'failed')::text) END AS failed_total_chars,
                   jsonb_typeof(outputs_value -> 'resultsCoverage') AS coverage_kind,
                   outputs_value -> 'resultsCoverage' AS coverage_value,
                   CASE WHEN outputs_value -> 'resultsCoverage' IS NOT NULL THEN octet_length((outputs_value -> 'resultsCoverage')::text) END AS coverage_bytes
            FROM raw
        ), budgeted AS (
            SELECT assessed.*, coalesce(sum(coverage_bytes) OVER (), 0) AS total_coverage_bytes
            FROM assessed
        )
        SELECT ordinal, source_run_id, node_id, record_type,
               CASE WHEN record_type IN ('node.completed', 'node.failed', 'node.skipped') THEN occurred_at END AS completed_at,
               error_kind, error_prefix, error_total_chars,
               outputs_kind, count_kind, count_text, count_total_chars, failed_kind, failed_text, failed_total_chars,
               coverage_kind,
               CASE WHEN coverage_bytes <= @max_coverage_bytes AND total_coverage_bytes <= @max_total_coverage_bytes THEN coverage_value::text END AS coverage_text,
               coverage_bytes, total_coverage_bytes
        FROM budgeted
        ORDER BY ordinal
        """;

    private readonly IWorkflowRunViewMetadataReader _metadata;
    private readonly CodeSpaceDbContext _db;

    public WorkflowRunNodeObservationReader(IWorkflowRunViewMetadataReader metadata, CodeSpaceDbContext db)
    {
        _metadata = metadata;
        _db = db;
    }

    public async Task<WorkflowRunNodeObservation?> ReadAsync(WorkflowRunNodeObservationRequest request, CancellationToken cancellationToken)
    {
        var metadata = await _metadata.ReadAsync(request.RunId, request.TeamId, request.Scope, cancellationToken).ConfigureAwait(false);
        if (metadata is null) return null;

        var availability = CoverageAvailability(metadata);
        if (availability != WorkflowRunViewAvailability.Available) return Observation(metadata, availability);

        var topLevel = metadata.Cells.Where(value => value.IterationKey == WorkflowIterationKeys.TopLevel).ToList();
        if (topLevel.Count > WorkflowRunViewMetadataReader.MaximumTopologyNodes)
            return Observation(metadata, WorkflowRunViewAvailability.TooLarge);

        var mapNodeIds = MapFanout.MapNodesOf(metadata.Cells).Select(value => value.Node.NodeId).ToHashSet(StringComparer.Ordinal);
        var rows = await ReadLeavesAsync(request, topLevel, mapNodeIds, cancellationToken).ConfigureAwait(false);
        if (!Consistent(topLevel, rows)) return Observation(metadata, WorkflowRunViewAvailability.Unavailable);

        return new WorkflowRunNodeObservation
        {
            Metadata = metadata,
            Availability = WorkflowRunViewAvailability.Available,
            TopLevelLeaves = rows.ToDictionary(value => value.NodeId, value => ToLeaf(value, mapNodeIds.Contains(value.NodeId)), StringComparer.Ordinal),
        };
    }

    private async Task<List<LeafRow>> ReadLeavesAsync(WorkflowRunNodeObservationRequest request, IReadOnlyList<WorkflowRunCellMetadata> topLevel,
        IReadOnlySet<string> mapNodeIds, CancellationToken cancellationToken)
    {
        if (topLevel.Count == 0) return new List<LeafRow>();
        var connection = _db.Database.GetDbConnection();
        var closeAfter = connection.State != ConnectionState.Open;
        if (closeAfter) await _db.Database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = LeafSql;
            Add(command, "requested_run_id", DbType.Guid, request.RunId);
            Add(command, "team_id", DbType.Guid, request.TeamId);
            Add(command, "merge_lineage", DbType.Boolean, request.Scope == WorkflowRunViewScope.LineageMerged);
            Add(command, "child_source", DbType.String, WorkflowRunSourceTypes.ChildWorkflow);
            Add(command, "source_run_ids", topLevel.Select(value => value.SourceRunId).ToArray());
            Add(command, "node_ids", topLevel.Select(value => value.NodeId).ToArray());
            Add(command, "map_node_ids", mapNodeIds.ToArray());
            Add(command, "error_chars", DbType.Int32, MaximumErrorCharacters);
            Add(command, "number_chars", DbType.Int32, MaximumNumberCharacters);
            Add(command, "max_coverage_bytes", DbType.Int32, MaximumCoverageBytes);
            Add(command, "max_total_coverage_bytes", DbType.Int32, MaximumTotalCoverageBytes);

            var rows = new List<LeafRow>(topLevel.Count);
            await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess, cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) rows.Add(ReadRow(reader));
            return rows;
        }
        finally
        {
            if (closeAfter) await _db.Database.CloseConnectionAsync().ConfigureAwait(false);
        }
    }

    private static LeafRow ReadRow(DbDataReader reader) => new()
    {
        Ordinal = reader.GetInt64(0),
        SourceRunId = reader.GetGuid(1),
        NodeId = reader.GetString(2),
        RecordType = reader.GetString(3),
        CompletedAt = NullableInstant(reader, 4),
        ErrorKind = NullableString(reader, 5),
        ErrorPrefix = NullableString(reader, 6),
        ErrorTotalCharacters = NullableInt32(reader, 7),
        OutputsKind = NullableString(reader, 8),
        CountKind = NullableString(reader, 9),
        CountText = NullableString(reader, 10),
        CountTotalCharacters = NullableInt32(reader, 11),
        FailedKind = NullableString(reader, 12),
        FailedText = NullableString(reader, 13),
        FailedTotalCharacters = NullableInt32(reader, 14),
        CoverageKind = NullableString(reader, 15),
        CoverageText = NullableString(reader, 16),
        CoverageBytes = NullableInt64(reader, 17),
        TotalCoverageBytes = reader.GetInt64(18),
    };

    private static bool Consistent(IReadOnlyList<WorkflowRunCellMetadata> expected, IReadOnlyList<LeafRow> rows)
    {
        if (expected.Count != rows.Count) return false;
        for (var index = 0; index < expected.Count; index++)
        {
            var cell = expected[index];
            var row = rows[index];
            if (row.Ordinal != index + 1 || row.SourceRunId != cell.SourceRunId || row.NodeId != cell.NodeId
                || Status(row.RecordType) != cell.Status || row.CompletedAt != cell.CompletedAt) return false;
        }
        return true;
    }

    private static WorkflowRunNodeLeafObservation ToLeaf(LeafRow row, bool isMap) => new()
    {
        ErrorState = ErrorState(row),
        ErrorPrefix = row.ErrorPrefix,
        MapMetrics = isMap ? MapMetrics(row) : null,
    };

    private static WorkflowRunNodeLeafState ErrorState(LeafRow row)
    {
        if (Status(row.RecordType) != NodeStatus.Failure) return WorkflowRunNodeLeafState.Missing;
        if (row.ErrorKind != "string" || row.ErrorTotalCharacters is null) return WorkflowRunNodeLeafState.Invalid;
        return row.ErrorTotalCharacters > MaximumErrorCharacters ? WorkflowRunNodeLeafState.Truncated : WorkflowRunNodeLeafState.Exact;
    }

    private static WorkflowRunMapMetricsObservation MapMetrics(LeafRow row)
    {
        var coverageState = CoverageState(row);
        JsonElement? coverage = null;
        if (coverageState == WorkflowRunNodeLeafState.Exact && row.CoverageText is not null)
        {
            try { coverage = JsonDocument.Parse(row.CoverageText).RootElement.Clone(); }
            catch (JsonException) { coverageState = WorkflowRunNodeLeafState.Invalid; }
        }

        return new WorkflowRunMapMetricsObservation
        {
            Count = ReadInt(row.CountKind, row.CountText, row.CountTotalCharacters),
            Failed = ReadInt(row.FailedKind, row.FailedText, row.FailedTotalCharacters),
            ResultsCoverageState = coverageState,
            ResultsCoverage = coverage,
        };
    }

    private static WorkflowRunNodeLeafState CoverageState(LeafRow row)
    {
        if (row.OutputsKind is null || row.CoverageKind is null) return WorkflowRunNodeLeafState.Missing;
        if (row.OutputsKind != "object") return WorkflowRunNodeLeafState.Invalid;
        if (row.CoverageBytes > MaximumCoverageBytes || row.TotalCoverageBytes > MaximumTotalCoverageBytes) return WorkflowRunNodeLeafState.Truncated;
        return row.CoverageText is null ? WorkflowRunNodeLeafState.Invalid : WorkflowRunNodeLeafState.Exact;
    }

    private static int ReadInt(string? kind, string? text, int? totalCharacters)
    {
        if (kind != "number" || text is null || totalCharacters > MaximumNumberCharacters) return 0;
        try { return JsonDocument.Parse(text).RootElement.TryGetInt32(out var value) ? value : 0; }
        catch (JsonException) { return 0; }
    }

    private static WorkflowRunNodeObservation Observation(WorkflowRunViewMetadata metadata, WorkflowRunViewAvailability availability) => new()
    {
        Metadata = metadata,
        Availability = availability,
        TopLevelLeaves = new Dictionary<string, WorkflowRunNodeLeafObservation>(StringComparer.Ordinal),
    };

    private static WorkflowRunViewAvailability CoverageAvailability(WorkflowRunViewMetadata metadata)
    {
        var values = new[] { metadata.CellsAvailability, metadata.LinksAvailability, metadata.TopologyAvailability };
        if (values.Contains(WorkflowRunViewAvailability.Corrupt)) return WorkflowRunViewAvailability.Corrupt;
        if (values.Contains(WorkflowRunViewAvailability.TooLarge)) return WorkflowRunViewAvailability.TooLarge;
        if (values.Contains(WorkflowRunViewAvailability.Unavailable)) return WorkflowRunViewAvailability.Unavailable;
        return values.Contains(WorkflowRunViewAvailability.Truncated) ? WorkflowRunViewAvailability.Truncated : WorkflowRunViewAvailability.Available;
    }

    private static NodeStatus Status(string recordType) => recordType switch
    {
        WorkflowRunRecordTypes.NodeStarted => NodeStatus.Running,
        WorkflowRunRecordTypes.NodeCompleted => NodeStatus.Success,
        WorkflowRunRecordTypes.NodeFailed => NodeStatus.Failure,
        WorkflowRunRecordTypes.NodeSkipped => NodeStatus.Skipped,
        WorkflowRunRecordTypes.NodeSuspended => NodeStatus.Suspended,
        _ => NodeStatus.Pending,
    };

    private static string? NullableString(DbDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
    private static int? NullableInt32(DbDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetInt32(ordinal);
    private static long? NullableInt64(DbDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetInt64(ordinal);
    private static DateTimeOffset? NullableInstant(DbDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetFieldValue<DateTimeOffset>(ordinal);

    private static void Add(DbCommand command, string name, DbType type, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.DbType = type;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }

    private static void Add(DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }

    private sealed record LeafRow
    {
        public required long Ordinal { get; init; }
        public required Guid SourceRunId { get; init; }
        public required string NodeId { get; init; }
        public required string RecordType { get; init; }
        public DateTimeOffset? CompletedAt { get; init; }
        public string? ErrorKind { get; init; }
        public string? ErrorPrefix { get; init; }
        public int? ErrorTotalCharacters { get; init; }
        public string? OutputsKind { get; init; }
        public string? CountKind { get; init; }
        public string? CountText { get; init; }
        public int? CountTotalCharacters { get; init; }
        public string? FailedKind { get; init; }
        public string? FailedText { get; init; }
        public int? FailedTotalCharacters { get; init; }
        public string? CoverageKind { get; init; }
        public string? CoverageText { get; init; }
        public long? CoverageBytes { get; init; }
        public required long TotalCoverageBytes { get; init; }
    }
}
