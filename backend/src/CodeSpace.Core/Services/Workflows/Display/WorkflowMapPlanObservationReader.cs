using System.Data;
using System.Data.Common;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Services.Workflows.Artifacts;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Dtos.Workflows;
using CodeSpace.Messages.Enums;
using Microsoft.EntityFrameworkCore;

namespace CodeSpace.Core.Services.Workflows.Display;

/// <summary>
/// Reads only the pinned map bindings and their selected producer leaves. Inline values are projected and budgeted in
/// PostgreSQL; offloaded values are fetched through one batch range read and accepted only when the complete object is
/// integrity verified. The engine's full-detail/replay authority is deliberately not used by this display projection.
/// </summary>
public sealed class WorkflowMapPlanObservationReader : IWorkflowMapPlanObservationReader, IScopedDependency
{
    public const int MaximumPlanners = 100;
    public const int MaximumSubtasks = 500;
    public const int MaximumBindingCharacters = 2048;
    public const int MaximumLeafBytes = 512 * 1024;
    public const int MaximumTotalInlineBytes = 2 * 1024 * 1024;
    public const int MaximumArtifactReferences = 8;
    public const int MaximumErrorCharacters = 2048;
    public const int MaximumTextCharacters = 512;
    public const int MaximumNumberCharacters = 64;

    private const string JsonContentType = "application/json";
    private static readonly Regex NodeRef = new(@"\{\{\s*nodes\.([A-Za-z0-9_-]+)\.", RegexOptions.Compiled);

    internal const string BindingSql = """
        WITH source AS (
            SELECT coalesce(run.definition_snapshot_jsonb, version.definition_jsonb) AS definition
            FROM workflow_run AS run
            LEFT JOIN workflow_version AS version ON version.workflow_id = run.workflow_id AND version.version = run.workflow_version
            WHERE run.team_id = @team_id AND run.id = @run_id
            LIMIT 1
        ), assessed AS (
            SELECT definition,
                   CASE WHEN definition IS NULL THEN 'Unavailable'
                        WHEN octet_length(definition::text) > @max_definition_bytes THEN 'TooLarge'
                        WHEN jsonb_typeof(definition) IS DISTINCT FROM 'object'
                          OR jsonb_typeof(definition -> 'nodes') IS DISTINCT FROM 'array' THEN 'Corrupt'
                        WHEN jsonb_array_length(definition -> 'nodes') > @max_nodes THEN 'TooLarge'
                        ELSE 'Available' END AS availability
            FROM source
        ), maps AS (
            SELECT node ->> 'id' AS map_id,
                   CASE WHEN jsonb_typeof(node -> 'inputs') = 'object'
                          AND jsonb_typeof(node #> '{inputs,items}') = 'string'
                          AND char_length(node #>> '{inputs,items}') <= @max_binding_chars
                        THEN node #>> '{inputs,items}' END AS items_binding,
                   CASE WHEN jsonb_typeof(node -> 'id') = 'string'
                          AND char_length(node ->> 'id') BETWEEN 1 AND @max_identity_chars
                          AND jsonb_typeof(node -> 'typeKey') = 'string'
                          AND node ->> 'typeKey' = @map_type THEN false ELSE true END AS invalid
            FROM assessed
            CROSS JOIN LATERAL jsonb_array_elements(CASE WHEN availability = 'Available' THEN definition -> 'nodes' ELSE '[]'::jsonb END) AS item(node)
            WHERE node ->> 'typeKey' = @map_type
        )
        SELECT assessed.availability, maps.map_id, maps.items_binding, coalesce(maps.invalid, false)
        FROM assessed
        LEFT JOIN maps ON true
        ORDER BY maps.map_id COLLATE "C"
        """;

    internal const string ProducerSql = """
        WITH lineage AS (
            SELECT run_id, attempt_ordinal
            FROM unnest(@run_ids::uuid[]) WITH ORDINALITY AS requested(run_id, attempt_ordinal)
        ), attempt_cells AS (
            SELECT record.run_id, lineage.attempt_ordinal, record.node_id,
                   (array_agg(record.id ORDER BY record.sequence DESC))[1] AS state_record_id,
                   (array_agg(record.sequence ORDER BY record.sequence DESC))[1] AS state_record_sequence
            FROM workflow_run_record AS record
            INNER JOIN lineage ON lineage.run_id = record.run_id
            WHERE record.run_id = ANY(@run_ids)
              AND record.node_id = ANY(@producer_ids)
              AND record.iteration_key = ''
              AND record.record_type LIKE 'node.%'
            GROUP BY record.run_id, lineage.attempt_ordinal, record.node_id
        ), selected AS (
            SELECT DISTINCT ON (node_id) run_id, node_id, state_record_id, state_record_sequence
            FROM attempt_cells
            ORDER BY node_id, attempt_ordinal DESC
        ), raw AS (
            SELECT selected.node_id, selected.state_record_id, selected.state_record_sequence,
                   record.record_type, record.occurred_at, record.payload_json -> 'outputs' AS outputs,
                   CASE WHEN record.record_type = 'node.failed' THEN record.payload_json -> 'error' END AS error_value
            FROM selected
            INNER JOIN workflow_run_record AS record
              ON record.id = selected.state_record_id
             AND record.run_id = selected.run_id
             AND record.node_id = selected.node_id
             AND record.iteration_key = ''
             AND record.sequence = selected.state_record_sequence
        ), leaves AS (
            SELECT raw.*,
                   CASE WHEN jsonb_typeof(outputs -> 'json') = 'object' AND NOT (outputs -> 'json' ? '$artifact_ref')
                        THEN outputs #> '{json,subtasks}' END AS json_subtasks,
                   outputs -> 'json' AS json_value,
                   outputs -> 'items' AS items_value,
                   jsonb_typeof(error_value) AS error_kind,
                   CASE WHEN jsonb_typeof(error_value) = 'string' THEN left(error_value #>> '{}', @max_error_chars) END AS error_prefix,
                   CASE WHEN jsonb_typeof(error_value) = 'string' THEN char_length(error_value #>> '{}') END AS error_chars,
                   CASE WHEN jsonb_typeof(outputs -> 'model') = 'string' THEN left(outputs ->> 'model', @max_text_chars) END AS model_prefix,
                   CASE WHEN jsonb_typeof(outputs -> 'model') = 'string' THEN char_length(outputs ->> 'model') END AS model_chars,
                   jsonb_typeof(outputs -> 'model') AS model_kind,
                   CASE WHEN outputs -> 'inputTokens' IS NOT NULL THEN left((outputs -> 'inputTokens')::text, @max_number_chars) END AS input_text,
                   CASE WHEN outputs -> 'outputTokens' IS NOT NULL THEN left((outputs -> 'outputTokens')::text, @max_number_chars) END AS output_text,
                   CASE WHEN outputs -> 'costUsd' IS NOT NULL THEN left((outputs -> 'costUsd')::text, @max_number_chars) END AS cost_text
            FROM raw
        ), sized AS (
            SELECT leaves.*,
                   jsonb_typeof(json_subtasks) AS json_subtasks_kind,
                   CASE WHEN json_subtasks IS NOT NULL THEN octet_length(json_subtasks::text) END AS json_subtasks_bytes,
                   jsonb_typeof(items_value) AS items_kind,
                   CASE WHEN items_value IS NOT NULL THEN octet_length(items_value::text) END AS items_bytes,
                   coalesce(CASE WHEN json_subtasks IS NOT NULL THEN octet_length(json_subtasks::text) END, 0)
                     + coalesce(CASE WHEN items_value IS NOT NULL THEN octet_length(items_value::text) END, 0) AS row_inline_bytes
            FROM leaves
        ), budgeted AS (
            SELECT sized.*, sum(row_inline_bytes) OVER (ORDER BY node_id COLLATE "C") AS cumulative_inline_bytes
            FROM sized
        )
        SELECT node_id, state_record_id, state_record_sequence, record_type, occurred_at,
               error_kind, error_prefix, error_chars,
               json_subtasks_kind,
               CASE WHEN json_subtasks_bytes <= @max_leaf_bytes AND cumulative_inline_bytes <= @max_total_inline_bytes THEN json_subtasks::text END,
               json_subtasks_bytes,
               items_kind,
               CASE WHEN items_bytes <= @max_leaf_bytes AND cumulative_inline_bytes <= @max_total_inline_bytes THEN items_value::text END,
               items_bytes,
               jsonb_typeof(json_value),
               coalesce(jsonb_typeof(json_value) = 'object' AND json_value ? '$artifact_ref', false),
               CASE WHEN jsonb_typeof(json_value) = 'object' AND json_value ? '$artifact_ref' THEN
                   (SELECT count(*) FROM jsonb_object_keys(json_value)) = 1
                   AND jsonb_typeof(json_value -> '$artifact_ref') = 'object'
                   AND (SELECT count(*) FROM jsonb_object_keys(json_value -> '$artifact_ref')) = 3
                   AND jsonb_typeof(json_value #> '{$artifact_ref,id}') = 'string'
                   AND jsonb_typeof(json_value #> '{$artifact_ref,size_bytes}') = 'number'
                   AND jsonb_typeof(json_value #> '{$artifact_ref,content_type}') = 'string'
                   AND char_length(json_value #>> '{$artifact_ref,id}') <= @max_ref_id_chars
                   AND char_length(json_value #>> '{$artifact_ref,size_bytes}') <= @max_declared_size_chars
                   AND char_length(json_value #>> '{$artifact_ref,content_type}') <= @max_content_type_chars ELSE false END,
               CASE WHEN jsonb_typeof(json_value) = 'object' AND json_value ? '$artifact_ref' THEN left(json_value #>> '{$artifact_ref,id}', @max_ref_id_chars) END,
               CASE WHEN jsonb_typeof(json_value) = 'object' AND json_value ? '$artifact_ref' THEN left(json_value #>> '{$artifact_ref,size_bytes}', @max_declared_size_chars) END,
               CASE WHEN jsonb_typeof(json_value) = 'object' AND json_value ? '$artifact_ref' THEN left(json_value #>> '{$artifact_ref,content_type}', @max_content_type_chars) END,
               coalesce(jsonb_typeof(items_value) = 'object' AND items_value ? '$artifact_ref', false),
               CASE WHEN jsonb_typeof(items_value) = 'object' AND items_value ? '$artifact_ref' THEN
                   (SELECT count(*) FROM jsonb_object_keys(items_value)) = 1
                   AND jsonb_typeof(items_value -> '$artifact_ref') = 'object'
                   AND (SELECT count(*) FROM jsonb_object_keys(items_value -> '$artifact_ref')) = 3
                   AND jsonb_typeof(items_value #> '{$artifact_ref,id}') = 'string'
                   AND jsonb_typeof(items_value #> '{$artifact_ref,size_bytes}') = 'number'
                   AND jsonb_typeof(items_value #> '{$artifact_ref,content_type}') = 'string'
                   AND char_length(items_value #>> '{$artifact_ref,id}') <= @max_ref_id_chars
                   AND char_length(items_value #>> '{$artifact_ref,size_bytes}') <= @max_declared_size_chars
                   AND char_length(items_value #>> '{$artifact_ref,content_type}') <= @max_content_type_chars ELSE false END,
               CASE WHEN jsonb_typeof(items_value) = 'object' AND items_value ? '$artifact_ref' THEN left(items_value #>> '{$artifact_ref,id}', @max_ref_id_chars) END,
               CASE WHEN jsonb_typeof(items_value) = 'object' AND items_value ? '$artifact_ref' THEN left(items_value #>> '{$artifact_ref,size_bytes}', @max_declared_size_chars) END,
               CASE WHEN jsonb_typeof(items_value) = 'object' AND items_value ? '$artifact_ref' THEN left(items_value #>> '{$artifact_ref,content_type}', @max_content_type_chars) END,
               model_kind, model_prefix, model_chars, input_text, output_text, cost_text,
               cumulative_inline_bytes
        FROM budgeted
        ORDER BY node_id COLLATE "C"
        """;

    private readonly IWorkflowRunViewAdmission _admission;
    private readonly CodeSpaceDbContext _db;
    private readonly IArtifactRangeReader _artifacts;

    public WorkflowMapPlanObservationReader(IWorkflowRunViewAdmission admission, CodeSpaceDbContext db, IArtifactRangeReader artifacts)
    {
        _admission = admission;
        _db = db;
        _artifacts = artifacts;
    }

    public async Task<WorkflowMapPlanObservation?> ReadAsync(WorkflowMapPlanObservationRequest request, CancellationToken cancellationToken)
    {
        var admitted = await _admission.AdmitAsync(request.RunId, request.TeamId, request.Scope, cancellationToken).ConfigureAwait(false);
        if (admitted is null) return null;
        if (admitted.LineageAvailability != WorkflowRunViewAvailability.Available)
            return Observation(admitted, admitted.LineageAvailability);

        var bindings = await ReadBindingsAsync(request, cancellationToken).ConfigureAwait(false);
        if (bindings.Availability != WorkflowRunViewAvailability.Available) return Observation(admitted, bindings.Availability);

        var producerIds = bindings.Rows.Select(ProducerId).Where(value => value is not null).Select(value => value!)
            .Distinct(StringComparer.Ordinal).ToList();
        if (producerIds.Count == 0) return Observation(admitted, WorkflowRunViewAvailability.Available);
        if (producerIds.Count > MaximumPlanners) return Observation(admitted, WorkflowRunViewAvailability.TooLarge);

        var rows = await ReadProducersAsync(admitted, producerIds, cancellationToken).ConfigureAwait(false);
        var references = rows.SelectMany(ReferencesOf).Distinct().ToList();
        IReadOnlyDictionary<Guid, ArtifactRangeReadResult> artifacts = new Dictionary<Guid, ArtifactRangeReadResult>();
        if (references.Count is > 0 and <= MaximumArtifactReferences)
            artifacts = await _artifacts.ReadRangesAsync(new ArtifactRangesReadRequest(request.TeamId, references, 0, MaximumLeafBytes + 1), cancellationToken).ConfigureAwait(false);

        var planners = rows.Select(row => ToObservation(row, artifacts, references.Count > MaximumArtifactReferences)).ToList();
        return new WorkflowMapPlanObservation
        {
            RunId = admitted.Header.Id,
            Availability = WorkflowRunViewAvailability.Available,
            AnchorAt = admitted.Header.CompletedAt ?? admitted.Header.StartedAt ?? admitted.Header.CreatedDate,
            Planners = planners,
        };
    }

    private async Task<BindingRead> ReadBindingsAsync(WorkflowMapPlanObservationRequest request, CancellationToken cancellationToken)
    {
        var connection = _db.Database.GetDbConnection();
        var closeAfter = connection.State != ConnectionState.Open;
        if (closeAfter) await _db.Database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = BindingSql;
            Add(command, "team_id", DbType.Guid, request.TeamId);
            Add(command, "run_id", DbType.Guid, request.RunId);
            Add(command, "max_definition_bytes", DbType.Int32, WorkflowRunViewMetadataReader.MaximumDefinitionJsonBytes);
            Add(command, "max_nodes", DbType.Int32, WorkflowRunViewMetadataReader.MaximumTopologyNodes);
            Add(command, "max_binding_chars", DbType.Int32, MaximumBindingCharacters);
            Add(command, "max_identity_chars", DbType.Int32, WorkflowRunViewMetadataReader.MaximumIdentityCharacters);
            Add(command, "map_type", DbType.String, MapFanout.ContainerKind);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            var rows = new List<BindingRow>();
            WorkflowRunViewAvailability? availability = null;
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                availability ??= Enum.TryParse<WorkflowRunViewAvailability>(reader.GetString(0), out var parsed) ? parsed : WorkflowRunViewAvailability.Corrupt;
                if (!reader.IsDBNull(1)) rows.Add(new BindingRow(reader.GetString(1), NullableString(reader, 2), reader.GetBoolean(3)));
            }
            return rows.Any(value => value.Invalid)
                ? new BindingRead(WorkflowRunViewAvailability.Corrupt, Array.Empty<BindingRow>())
                : new BindingRead(availability ?? WorkflowRunViewAvailability.Unavailable, rows);
        }
        finally
        {
            if (closeAfter) await _db.Database.CloseConnectionAsync().ConfigureAwait(false);
        }
    }

    private async Task<List<ProducerRow>> ReadProducersAsync(WorkflowRunViewAdmission admitted, IReadOnlyList<string> producerIds, CancellationToken cancellationToken)
    {
        var connection = _db.Database.GetDbConnection();
        var closeAfter = connection.State != ConnectionState.Open;
        if (closeAfter) await _db.Database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = ProducerSql;
            Add(command, "run_ids", admitted.Lineage.Select(value => value.Id).ToArray());
            Add(command, "producer_ids", producerIds.ToArray());
            Add(command, "max_error_chars", DbType.Int32, MaximumErrorCharacters);
            Add(command, "max_text_chars", DbType.Int32, MaximumTextCharacters);
            Add(command, "max_number_chars", DbType.Int32, MaximumNumberCharacters);
            Add(command, "max_leaf_bytes", DbType.Int32, MaximumLeafBytes);
            Add(command, "max_total_inline_bytes", DbType.Int32, MaximumTotalInlineBytes);
            Add(command, "max_ref_id_chars", DbType.Int32, WorkflowRunCellFieldReader.MaximumArtifactIdCharacters);
            Add(command, "max_declared_size_chars", DbType.Int32, WorkflowRunCellFieldReader.MaximumDeclaredSizeCharacters);
            Add(command, "max_content_type_chars", DbType.Int32, WorkflowRunCellFieldReader.MaximumContentTypeCharacters);
            var rows = new List<ProducerRow>();
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) rows.Add(ReadProducer(reader));
            return rows;
        }
        finally
        {
            if (closeAfter) await _db.Database.CloseConnectionAsync().ConfigureAwait(false);
        }
    }

    private static ProducerRow ReadProducer(DbDataReader reader) => new()
    {
        NodeId = reader.GetString(0), RecordId = reader.GetGuid(1), RecordSequence = reader.GetInt64(2), RecordType = reader.GetString(3), OccurredAt = reader.GetFieldValue<DateTimeOffset>(4),
        ErrorKind = NullableString(reader, 5), ErrorPrefix = NullableString(reader, 6), ErrorCharacters = NullableInt32(reader, 7),
        JsonSubtasksKind = NullableString(reader, 8), JsonSubtasksText = NullableString(reader, 9), JsonSubtasksBytes = NullableInt64(reader, 10),
        ItemsKind = NullableString(reader, 11), ItemsText = NullableString(reader, 12), ItemsBytes = NullableInt64(reader, 13),
        JsonKind = NullableString(reader, 14), JsonHasRef = reader.GetBoolean(15), JsonCanonicalRef = reader.GetBoolean(16),
        JsonRef = Ref(reader, 17, 18, 19), ItemsHasRef = reader.GetBoolean(20), ItemsCanonicalRef = reader.GetBoolean(21), ItemsRef = Ref(reader, 22, 23, 24),
        ModelKind = NullableString(reader, 25), ModelPrefix = NullableString(reader, 26), ModelCharacters = NullableInt32(reader, 27),
        InputText = NullableString(reader, 28), OutputText = NullableString(reader, 29), CostText = NullableString(reader, 30), CumulativeInlineBytes = reader.GetInt64(31),
    };

    private static WorkflowMapPlannerObservation ToObservation(ProducerRow row, IReadOnlyDictionary<Guid, ArtifactRangeReadResult> artifacts, bool artifactSetTooLarge)
    {
        var subtasks = ReadSubtasks(row, artifacts, artifactSetTooLarge);
        return new WorkflowMapPlannerObservation
        {
            ProducerNodeId = row.NodeId,
            Status = Status(row.RecordType),
            CompletedAt = row.RecordType is WorkflowRunRecordTypes.NodeCompleted or WorkflowRunRecordTypes.NodeFailed or WorkflowRunRecordTypes.NodeSkipped ? row.OccurredAt : null,
            StateRecordId = row.RecordId,
            StateRecordSequence = row.RecordSequence,
            ErrorState = ErrorState(row),
            ErrorPrefix = row.ErrorPrefix,
            SubtasksState = subtasks.State,
            SubtasksTotalCount = subtasks.TotalCount,
            Subtasks = subtasks.Value,
            ModelUsageState = ModelState(row),
            ModelOutputs = ModelOutputs(row),
        };
    }

    private static LeafResult ReadSubtasks(ProducerRow row, IReadOnlyDictionary<Guid, ArtifactRangeReadResult> artifacts, bool artifactSetTooLarge)
    {
        if (row.JsonHasRef)
        {
            var fromArtifact = ReadArtifactLeaf(row.JsonCanonicalRef, row.JsonRef, artifacts, artifactSetTooLarge, root =>
                root.ValueKind == JsonValueKind.Object && root.TryGetProperty("subtasks", out var value) ? value : (JsonElement?)null);
            if (fromArtifact.State != WorkflowMapPlanLeafState.Missing) return fromArtifact;
        }
        else if (row.JsonSubtasksKind is not null)
        {
            return ReadInlineLeaf(row.JsonSubtasksKind, row.JsonSubtasksText, row.JsonSubtasksBytes, row.CumulativeInlineBytes);
        }

        if (row.ItemsHasRef) return ReadArtifactLeaf(row.ItemsCanonicalRef, row.ItemsRef, artifacts, artifactSetTooLarge, root => root);
        if (row.ItemsKind is not null) return ReadInlineLeaf(row.ItemsKind, row.ItemsText, row.ItemsBytes, row.CumulativeInlineBytes);
        return new LeafResult(WorkflowMapPlanLeafState.Missing, 0, null);
    }

    private static LeafResult ReadArtifactLeaf(bool canonical, ArtifactRef? reference, IReadOnlyDictionary<Guid, ArtifactRangeReadResult> artifacts,
        bool artifactSetTooLarge, Func<JsonElement, JsonElement?> select)
    {
        if (!canonical || reference is null) return new LeafResult(WorkflowMapPlanLeafState.Invalid, 0, null);
        if (reference.SizeBytes > MaximumLeafBytes || artifactSetTooLarge) return new LeafResult(WorkflowMapPlanLeafState.Truncated, 0, null);
        if (!string.Equals(reference.ContentType, JsonContentType, StringComparison.Ordinal)) return new LeafResult(WorkflowMapPlanLeafState.Invalid, 0, null);
        if (!artifacts.TryGetValue(reference.Id, out var read) || read.State != ArtifactRangeReadState.Available)
            return new LeafResult(WorkflowMapPlanLeafState.Unavailable, 0, null);
        if (read.TotalLength != reference.SizeBytes || !read.IntegrityVerified || read.Bytes is null
            || !string.Equals(read.ContentType, JsonContentType, StringComparison.Ordinal)) return new LeafResult(WorkflowMapPlanLeafState.Invalid, 0, null);
        try
        {
            using var document = JsonDocument.Parse(read.Bytes);
            var selected = select(document.RootElement);
            return selected is null ? new LeafResult(WorkflowMapPlanLeafState.Missing, 0, null) : AssessArray(selected.Value);
        }
        catch (JsonException)
        {
            return new LeafResult(WorkflowMapPlanLeafState.Invalid, 0, null);
        }
    }

    private static LeafResult ReadInlineLeaf(string kind, string? text, long? bytes, long cumulativeBytes)
    {
        if (kind != "array") return new LeafResult(WorkflowMapPlanLeafState.Invalid, 0, null);
        if (bytes > MaximumLeafBytes || cumulativeBytes > MaximumTotalInlineBytes) return new LeafResult(WorkflowMapPlanLeafState.Truncated, 0, null);
        if (text is null) return new LeafResult(WorkflowMapPlanLeafState.Invalid, 0, null);
        try
        {
            using var document = JsonDocument.Parse(text);
            return AssessArray(document.RootElement);
        }
        catch (JsonException)
        {
            return new LeafResult(WorkflowMapPlanLeafState.Invalid, 0, null);
        }
    }

    private static LeafResult AssessArray(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Array) return new LeafResult(WorkflowMapPlanLeafState.Invalid, 0, null);
        var count = value.GetArrayLength();
        if (count > MaximumSubtasks || value.GetRawText().Length > MaximumLeafBytes) return new LeafResult(WorkflowMapPlanLeafState.Truncated, count, null);
        foreach (var item in value.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String && (item.GetString()?.Length ?? 0) > MaximumTextCharacters)
                return new LeafResult(WorkflowMapPlanLeafState.Truncated, count, null);
            if (item.ValueKind == JsonValueKind.Object && (StringLength(item, "id") > MaximumTextCharacters || StringLength(item, "title") > MaximumTextCharacters))
                return new LeafResult(WorkflowMapPlanLeafState.Truncated, count, null);
        }
        return new LeafResult(WorkflowMapPlanLeafState.Exact, count, value.Clone());
    }

    private static WorkflowMapPlanLeafState ErrorState(ProducerRow row)
    {
        if (Status(row.RecordType) != NodeStatus.Failure) return WorkflowMapPlanLeafState.Missing;
        if (row.ErrorKind != "string" || row.ErrorCharacters is null) return WorkflowMapPlanLeafState.Invalid;
        return row.ErrorCharacters > MaximumErrorCharacters ? WorkflowMapPlanLeafState.Truncated : WorkflowMapPlanLeafState.Exact;
    }

    private static WorkflowMapPlanLeafState ModelState(ProducerRow row)
    {
        if (row.ModelKind is null) return WorkflowMapPlanLeafState.Missing;
        if (row.ModelKind != "string" || row.ModelPrefix is null || string.IsNullOrWhiteSpace(row.ModelPrefix)) return WorkflowMapPlanLeafState.Invalid;
        return row.ModelCharacters > MaximumTextCharacters ? WorkflowMapPlanLeafState.Truncated : WorkflowMapPlanLeafState.Exact;
    }

    private static JsonElement? ModelOutputs(ProducerRow row)
    {
        if (ModelState(row) != WorkflowMapPlanLeafState.Exact) return null;
        var fields = new Dictionary<string, object?> { ["model"] = row.ModelPrefix };
        if (TryInt(row.InputText, out var input)) fields["inputTokens"] = input;
        if (TryInt(row.OutputText, out var output)) fields["outputTokens"] = output;
        if (TryDecimal(row.CostText, out var cost)) fields["costUsd"] = cost;
        return JsonSerializer.SerializeToElement(fields);
    }

    private static IEnumerable<Guid> ReferencesOf(ProducerRow row)
    {
        if (row.JsonCanonicalRef && row.JsonRef is not null) yield return row.JsonRef.Id;
        if (row.ItemsCanonicalRef && row.ItemsRef is not null) yield return row.ItemsRef.Id;
    }

    private static string? ProducerId(BindingRow row)
    {
        if (row.Invalid || row.ItemsBinding is null) return null;
        var match = NodeRef.Match(row.ItemsBinding);
        return match.Success ? match.Groups[1].Value : null;
    }

    private static ArtifactRef? Ref(DbDataReader reader, int idOrdinal, int sizeOrdinal, int typeOrdinal)
    {
        if (reader.IsDBNull(idOrdinal) || reader.IsDBNull(sizeOrdinal) || reader.IsDBNull(typeOrdinal)) return null;
        return Guid.TryParse(reader.GetString(idOrdinal), out var id) && id != Guid.Empty && long.TryParse(reader.GetString(sizeOrdinal), out var size) && size >= 0
            ? new ArtifactRef(id, size, reader.GetString(typeOrdinal)) : null;
    }

    private static WorkflowMapPlanObservation Observation(WorkflowRunViewAdmission admitted, WorkflowRunViewAvailability availability) => new()
    {
        RunId = admitted.Header.Id,
        Availability = availability,
        AnchorAt = admitted.Header.CompletedAt ?? admitted.Header.StartedAt ?? admitted.Header.CreatedDate,
        Planners = Array.Empty<WorkflowMapPlannerObservation>(),
    };

    private static NodeStatus Status(string recordType) => recordType switch
    {
        WorkflowRunRecordTypes.NodeStarted => NodeStatus.Running,
        WorkflowRunRecordTypes.NodeCompleted => NodeStatus.Success,
        WorkflowRunRecordTypes.NodeFailed => NodeStatus.Failure,
        WorkflowRunRecordTypes.NodeSkipped => NodeStatus.Skipped,
        WorkflowRunRecordTypes.NodeSuspended => NodeStatus.Suspended,
        _ => NodeStatus.Pending,
    };

    private static int StringLength(JsonElement obj, string name) => obj.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString()?.Length ?? 0 : 0;
    private static bool TryInt(string? value, out int parsed)
    {
        parsed = 0;
        if (value is null || value.Length > MaximumNumberCharacters) return false;
        try { return JsonDocument.Parse(value).RootElement.TryGetInt32(out parsed); }
        catch (JsonException) { return false; }
    }
    private static bool TryDecimal(string? value, out decimal parsed)
    {
        parsed = 0;
        if (value is null || value.Length > MaximumNumberCharacters) return false;
        try { return JsonDocument.Parse(value).RootElement.TryGetDecimal(out parsed); }
        catch (JsonException) { return false; }
    }

    private static string? NullableString(DbDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
    private static int? NullableInt32(DbDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetInt32(ordinal);
    private static long? NullableInt64(DbDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetInt64(ordinal);

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

    private sealed record BindingRead(WorkflowRunViewAvailability Availability, IReadOnlyList<BindingRow> Rows);
    private sealed record BindingRow(string MapId, string? ItemsBinding, bool Invalid);
    private sealed record ArtifactRef(Guid Id, long SizeBytes, string ContentType);
    private sealed record LeafResult(WorkflowMapPlanLeafState State, int TotalCount, JsonElement? Value);

    private sealed record ProducerRow
    {
        public required string NodeId { get; init; }
        public required Guid RecordId { get; init; }
        public required long RecordSequence { get; init; }
        public required string RecordType { get; init; }
        public required DateTimeOffset OccurredAt { get; init; }
        public string? ErrorKind { get; init; }
        public string? ErrorPrefix { get; init; }
        public int? ErrorCharacters { get; init; }
        public string? JsonSubtasksKind { get; init; }
        public string? JsonSubtasksText { get; init; }
        public long? JsonSubtasksBytes { get; init; }
        public string? ItemsKind { get; init; }
        public string? ItemsText { get; init; }
        public long? ItemsBytes { get; init; }
        public string? JsonKind { get; init; }
        public required bool JsonHasRef { get; init; }
        public required bool JsonCanonicalRef { get; init; }
        public ArtifactRef? JsonRef { get; init; }
        public required bool ItemsHasRef { get; init; }
        public required bool ItemsCanonicalRef { get; init; }
        public ArtifactRef? ItemsRef { get; init; }
        public string? ModelKind { get; init; }
        public string? ModelPrefix { get; init; }
        public int? ModelCharacters { get; init; }
        public string? InputText { get; init; }
        public string? OutputText { get; init; }
        public string? CostText { get; init; }
        public required long CumulativeInlineBytes { get; init; }
    }
}
