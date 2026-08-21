using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Text;
using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Services.Workflows.Display.Exceptions;
using CodeSpace.Messages.Dtos.Workflows;
using CodeSpace.Messages.Queries.Workflows;
using Microsoft.EntityFrameworkCore;

namespace CodeSpace.Core.Services.Workflows.Display;

/// <summary>
/// Body-blind descriptor reader for one exact selected cell. PostgreSQL inspects only the earliest-start/latest-state
/// records' top-level JSON fields; no payload, inline field value, artifact id or provider byte crosses the public seam.
/// </summary>
public sealed class WorkflowRunCellFieldReader : IWorkflowRunCellFieldReader, IScopedDependency
{
    public const int MaximumFieldNameCharacters = 256;
    public const int MaximumFieldNameUtf8Bytes = 1024;
    public const int MaximumArtifactIdCharacters = 64;
    public const int MaximumDeclaredSizeCharacters = 32;
    public const int MaximumContentTypeCharacters = 255;
    private const string JsonContentType = "application/json";

    internal const string FieldSql = """
        WITH source AS MATERIALIZED (
            SELECT started.payload_json -> 'inputs' AS inputs_value,
                   state.payload_json -> 'outputs' AS outputs_value,
                   state.payload_json -> 'error' AS error_value,
                   started.id IS NOT NULL AS first_record_present
            FROM workflow_run_record AS state
            LEFT JOIN workflow_run_record AS started
              ON @first_record_id IS NOT NULL
             AND started.id = @first_record_id
             AND started.run_id = @source_run_id
             AND started.node_id = @node_id
             AND started.iteration_key = @iteration_key
             AND started.sequence = @first_record_sequence
             AND started.record_type = 'node.started'
            WHERE state.id = @state_record_id
              AND state.run_id = @source_run_id
              AND state.node_id = @node_id
              AND state.iteration_key = @iteration_key
              AND state.sequence = @state_record_sequence
              AND state.record_type LIKE 'node.%'
            LIMIT 1
        ), section_state AS (
            SELECT 0 AS section_rank,
                   CASE WHEN @first_record_id IS NULL THEN 'NotRecorded'
                        WHEN NOT first_record_present THEN 'Unavailable'
                        WHEN inputs_value IS NULL THEN 'NotRecorded'
                        WHEN jsonb_typeof(inputs_value) = 'object' THEN 'Available' ELSE 'Unavailable' END AS availability
            FROM source
            UNION ALL
            SELECT 1,
                   CASE WHEN outputs_value IS NULL THEN 'NotRecorded'
                        WHEN jsonb_typeof(outputs_value) = 'object' THEN 'Available' ELSE 'Unavailable' END
            FROM source
            UNION ALL
            SELECT 2,
                   CASE WHEN error_value IS NULL OR jsonb_typeof(error_value) = 'null' THEN 'NotRecorded'
                        WHEN jsonb_typeof(error_value) = 'string' THEN 'Available' ELSE 'Unavailable' END
            FROM source
        ), field_keys AS (
            SELECT 0 AS section_rank, names.field_name
            FROM source
            CROSS JOIN LATERAL jsonb_object_keys(CASE WHEN jsonb_typeof(inputs_value) = 'object' THEN inputs_value ELSE '{}'::jsonb END) AS names(field_name)
            UNION ALL
            SELECT 1, names.field_name
            FROM source
            CROSS JOIN LATERAL jsonb_object_keys(CASE WHEN jsonb_typeof(outputs_value) = 'object' THEN outputs_value ELSE '{}'::jsonb END) AS names(field_name)
            UNION ALL
            SELECT 2, ''::text
            FROM source
            WHERE jsonb_typeof(error_value) = 'string'
        ), page_keys AS MATERIALIZED (
            SELECT section_rank,
                   CASE WHEN char_length(field_name) <= @max_name_chars AND octet_length(field_name) <= @max_name_bytes THEN field_name END AS field_name,
                   NOT (char_length(field_name) <= @max_name_chars AND octet_length(field_name) <= @max_name_bytes) AS name_invalid
            FROM field_keys
            WHERE @cursor_section IS NULL
               OR section_rank > @cursor_section
               OR (section_rank = @cursor_section AND field_name COLLATE "C" > @cursor_name COLLATE "C")
            ORDER BY section_rank, field_name COLLATE "C"
            LIMIT @take
        ), page_values AS MATERIALIZED (
            SELECT page_keys.section_rank, page_keys.field_name, page_keys.name_invalid,
                   CASE page_keys.section_rank WHEN 0 THEN source.inputs_value -> page_keys.field_name
                                               WHEN 1 THEN source.outputs_value -> page_keys.field_name
                                               ELSE source.error_value END AS field_value
            FROM page_keys
            CROSS JOIN source
        ), page AS (
            SELECT section_rank, field_name, name_invalid,
                   coalesce(jsonb_typeof(field_value), 'null') AS json_kind,
                   coalesce(section_rank = 1 AND jsonb_typeof(field_value) = 'object' AND field_value ? '$artifact_ref', false) AS has_ref_marker,
                   CASE WHEN section_rank = 1 AND jsonb_typeof(field_value) = 'object' AND field_value ? '$artifact_ref'
                        THEN CASE WHEN jsonb_typeof(field_value -> '$artifact_ref') = 'object'
                                  THEN (SELECT count(*) FROM jsonb_object_keys(field_value)) = 1
                                   AND (SELECT count(*) FROM jsonb_object_keys(field_value -> '$artifact_ref')) = 3
                                   AND jsonb_typeof(field_value #> '{$artifact_ref,id}') = 'string'
                                   AND jsonb_typeof(field_value #> '{$artifact_ref,size_bytes}') = 'number'
                                   AND jsonb_typeof(field_value #> '{$artifact_ref,content_type}') = 'string'
                                   AND char_length(field_value #>> '{$artifact_ref,id}') <= @max_ref_id_chars
                                   AND char_length(field_value #>> '{$artifact_ref,size_bytes}') <= @max_declared_size_chars
                                   AND char_length(field_value #>> '{$artifact_ref,content_type}') <= @max_content_type_chars
                                  ELSE false END
                        ELSE false END AS canonical_ref,
                   CASE WHEN section_rank = 1 AND jsonb_typeof(field_value) = 'object' AND field_value ? '$artifact_ref'
                                  AND char_length(field_value #>> '{$artifact_ref,id}') <= @max_ref_id_chars
                        THEN field_value #>> '{$artifact_ref,id}' END AS ref_id,
                   CASE WHEN section_rank = 1 AND jsonb_typeof(field_value) = 'object' AND field_value ? '$artifact_ref'
                                  AND char_length(field_value #>> '{$artifact_ref,size_bytes}') <= @max_declared_size_chars
                        THEN field_value #>> '{$artifact_ref,size_bytes}' END AS declared_size,
                   CASE WHEN section_rank = 1 AND jsonb_typeof(field_value) = 'object' AND field_value ? '$artifact_ref'
                                  AND char_length(field_value #>> '{$artifact_ref,content_type}') <= @max_content_type_chars
                        THEN field_value #>> '{$artifact_ref,content_type}' END AS declared_content_type
            FROM page_values
        ), summary AS (
            SELECT max(availability) FILTER (WHERE section_rank = 0) AS inputs_availability,
                   max(availability) FILTER (WHERE section_rank = 1) AS outputs_availability,
                   max(availability) FILTER (WHERE section_rank = 2) AS error_availability
            FROM section_state
        )
        SELECT summary.inputs_availability, summary.outputs_availability, summary.error_availability,
               page.section_rank, page.field_name, page.name_invalid, page.json_kind, page.has_ref_marker,
               page.canonical_ref, page.ref_id, page.declared_size, page.declared_content_type
        FROM summary
        LEFT JOIN page ON true
        ORDER BY page.section_rank, page.field_name COLLATE "C"
        """;

    private readonly CodeSpaceDbContext _db;
    private readonly IWorkflowRunViewAdmission _admission;

    public WorkflowRunCellFieldReader(CodeSpaceDbContext db, IWorkflowRunViewAdmission admission)
    {
        _db = db;
        _admission = admission;
    }

    public async Task<WorkflowRunCellFieldPage?> ReadAsync(WorkflowRunCellFieldReadRequest request, CancellationToken cancellationToken)
    {
        var cursor = Validate(request);
        var admitted = await _admission.AdmitAsync(request.RequestedRunId, request.TeamId, request.Scope, cancellationToken).ConfigureAwait(false);
        if (admitted is null || admitted.LineageAvailability != WorkflowRunViewAvailability.Available) return null;
        if (!ValidCoordinate(request)) return null;

        var selected = await _admission.ReadSelectedCellsAsync(admitted,
            new WorkflowRunCellCoordinate(request.NodeId, request.IterationKey), take: 2, cancellationToken).ConfigureAwait(false);
        if (selected.Count != 1) return null;
        var cell = selected[0];
        if (cell.IdentityInvalid || cell.SourceRunId != request.SourceRunId || cell.NodeId != request.NodeId || cell.IterationKey != request.IterationKey
            || cell.StateRecordId == Guid.Empty || cell.StateRecordSequence <= 0
            || (cell.FirstStartedRecordId is null) != (cell.FirstStartedRecordSequence is null)) return null;
        if (cursor is { } value && !CursorMatches(value, cell)) return null;

        var read = await ReadFieldsAsync(request, cell, cursor, cancellationToken).ConfigureAwait(false);
        if (read is null) return null;
        if (read.InputsAvailability == WorkflowRunCellFieldAvailability.Unavailable
            || read.OutputsAvailability == WorkflowRunCellFieldAvailability.Unavailable
            || read.ErrorAvailability == WorkflowRunCellFieldAvailability.Unavailable)
        {
            return Page(request, cell, read, WorkflowRunCellFieldAvailability.Unavailable,
                Array.Empty<WorkflowRunCellFieldDescriptor>(), nextCursor: null);
        }

        var hasMore = read.Rows.Count > request.Limit;
        if (hasMore) read.Rows.RemoveAt(read.Rows.Count - 1);
        var hasInvalidName = read.Rows.Any(value => value.NameInvalid);
        if (hasInvalidName)
            return Page(request, cell, read, WorkflowRunCellFieldAvailability.NameTooLarge,
                Array.Empty<WorkflowRunCellFieldDescriptor>(), nextCursor: null);

        var metadata = await ReadArtifactMetadataAsync(request.TeamId, read.Rows, cancellationToken).ConfigureAwait(false);
        var descriptors = read.Rows.Select(value => Descriptor(value, metadata)).ToList();
        var next = hasMore && read.Rows.Count > 0 ? CursorFor(cell, read.Rows[^1]).Encode() : null;
        return Page(request, cell, read, hasMore ? WorkflowRunCellFieldAvailability.Truncated : WorkflowRunCellFieldAvailability.Available, descriptors, next);
    }

    private async Task<FieldRead?> ReadFieldsAsync(WorkflowRunCellFieldReadRequest request, WorkflowRunSelectedCell cell,
        WorkflowRunCellFieldCursor? cursor, CancellationToken cancellationToken)
    {
        var connection = _db.Database.GetDbConnection();
        var closeAfter = connection.State != ConnectionState.Open;
        if (closeAfter) await _db.Database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = FieldSql;
            Add(command, "source_run_id", DbType.Guid, cell.SourceRunId);
            Add(command, "node_id", DbType.String, cell.NodeId!);
            Add(command, "iteration_key", DbType.String, cell.IterationKey!);
            Add(command, "state_record_id", DbType.Guid, cell.StateRecordId);
            Add(command, "state_record_sequence", DbType.Int64, cell.StateRecordSequence);
            Add(command, "first_record_id", DbType.Guid, cell.FirstStartedRecordId is { } firstId ? firstId : DBNull.Value);
            Add(command, "first_record_sequence", DbType.Int64, cell.FirstStartedRecordSequence is { } firstSequence ? firstSequence : DBNull.Value);
            Add(command, "cursor_section", DbType.Int32, cursor is { } value ? (int)value.Section : DBNull.Value);
            Add(command, "cursor_name", DbType.String, cursor is { } named ? named.Name : DBNull.Value);
            Add(command, "max_name_chars", DbType.Int32, MaximumFieldNameCharacters);
            Add(command, "max_name_bytes", DbType.Int32, MaximumFieldNameUtf8Bytes);
            Add(command, "max_ref_id_chars", DbType.Int32, MaximumArtifactIdCharacters);
            Add(command, "max_declared_size_chars", DbType.Int32, MaximumDeclaredSizeCharacters);
            Add(command, "max_content_type_chars", DbType.Int32, MaximumContentTypeCharacters);
            Add(command, "take", DbType.Int32, request.Limit + 1);

            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            FieldRead? result = null;
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                if (result is null)
                {
                    if (reader.IsDBNull(0) || reader.IsDBNull(1) || reader.IsDBNull(2)) return null;
                    result = new FieldRead(ParseAvailability(reader.GetString(0)), ParseAvailability(reader.GetString(1)),
                        ParseAvailability(reader.GetString(2)), new List<FieldRow>());
                }
                if (reader.IsDBNull(3)) continue;
                result.Rows.Add(new FieldRow
                {
                    Section = (WorkflowRunCellFieldSection)reader.GetInt32(3), Name = NullableString(reader, 4), NameInvalid = reader.GetBoolean(5),
                    JsonKind = reader.GetString(6), HasRefMarker = reader.GetBoolean(7), CanonicalRef = reader.GetBoolean(8),
                    RefId = NullableString(reader, 9), DeclaredSize = NullableString(reader, 10), DeclaredContentType = NullableString(reader, 11),
                });
            }
            return result;
        }
        finally
        {
            if (closeAfter) await _db.Database.CloseConnectionAsync().ConfigureAwait(false);
        }
    }

    private async Task<IReadOnlyDictionary<Guid, ArtifactMetadataRow>> ReadArtifactMetadataAsync(Guid teamId,
        IReadOnlyCollection<FieldRow> rows, CancellationToken cancellationToken)
    {
        var ids = rows.Where(value => value.HasRefMarker && value.CanonicalRef && Guid.TryParse(value.RefId, out _))
            .Select(value => Guid.Parse(value.RefId!)).Distinct().ToArray();
        if (ids.Length == 0) return new Dictionary<Guid, ArtifactMetadataRow>();

        return await _db.WorkflowArtifact.AsNoTracking().Where(value => value.TeamId == teamId && ids.Contains(value.Id))
            .Select(value => new ArtifactMetadataRow(value.Id, value.SizeBytes, value.Sha256, value.ContentType == JsonContentType))
            .ToDictionaryAsync(value => value.Id, cancellationToken).ConfigureAwait(false);
    }

    private static WorkflowRunCellFieldDescriptor Descriptor(FieldRow row, IReadOnlyDictionary<Guid, ArtifactMetadataRow> metadata)
    {
        if (!row.HasRefMarker)
            return NewDescriptor(row, WorkflowRunCellFieldMaterialization.Inline, DescriptorOutcome.AvailableInline);
        if (!row.CanonicalRef || !Guid.TryParse(row.RefId, out var artifactId)
            || !long.TryParse(row.DeclaredSize, NumberStyles.None, CultureInfo.InvariantCulture, out var declaredSize) || declaredSize < 0)
        {
            return NewDescriptor(row, WorkflowRunCellFieldMaterialization.Artifact,
                DescriptorOutcome.Failed(WorkflowRunCellFieldAvailability.CorruptReference, WorkflowRunCellFieldProblemCode.MalformedReference));
        }
        if (row.DeclaredContentType != JsonContentType)
            return NewDescriptor(row, WorkflowRunCellFieldMaterialization.Artifact,
                DescriptorOutcome.Failed(WorkflowRunCellFieldAvailability.CorruptReference, WorkflowRunCellFieldProblemCode.DeclaredContentTypeMismatch));
        if (!metadata.TryGetValue(artifactId, out var stored))
            return NewDescriptor(row, WorkflowRunCellFieldMaterialization.Artifact,
                DescriptorOutcome.Failed(WorkflowRunCellFieldAvailability.Unavailable, WorkflowRunCellFieldProblemCode.ArtifactMetadataMissing));
        if (stored.SizeBytes != declaredSize)
            return NewDescriptor(row, WorkflowRunCellFieldMaterialization.Artifact,
                DescriptorOutcome.Failed(WorkflowRunCellFieldAvailability.CorruptReference, WorkflowRunCellFieldProblemCode.DeclaredSizeMismatch));
        if (!stored.HasExpectedContentType)
            return NewDescriptor(row, WorkflowRunCellFieldMaterialization.Artifact,
                DescriptorOutcome.Failed(WorkflowRunCellFieldAvailability.CorruptReference, WorkflowRunCellFieldProblemCode.StoredContentTypeMismatch));

        return NewDescriptor(row, WorkflowRunCellFieldMaterialization.Artifact, DescriptorOutcome.AvailableArtifact(stored.SizeBytes, stored.Sha256));
    }

    private static WorkflowRunCellFieldDescriptor NewDescriptor(FieldRow row, WorkflowRunCellFieldMaterialization materialization,
        DescriptorOutcome outcome) => new()
    {
        Section = row.Section,
        Name = row.Section == WorkflowRunCellFieldSection.Error ? null : row.Name,
        JsonKind = ParseKind(row.JsonKind),
        Materialization = materialization,
        Availability = outcome.Availability,
        TotalBytes = outcome.TotalBytes,
        Sha256 = outcome.Sha256,
        ContentType = JsonContentType,
        ProblemCode = outcome.ProblemCode,
    };

    private static WorkflowRunCellFieldPage Page(WorkflowRunCellFieldReadRequest request, WorkflowRunSelectedCell cell,
        FieldRead read, WorkflowRunCellFieldAvailability availability, IReadOnlyList<WorkflowRunCellFieldDescriptor> fields,
        string? nextCursor) => new()
    {
        RequestedRunId = request.RequestedRunId,
        Scope = request.Scope,
        SourceRunId = cell.SourceRunId,
        NodeId = cell.NodeId!,
        IterationKey = cell.IterationKey!,
        StateRecordId = cell.StateRecordId,
        StateRecordSequence = cell.StateRecordSequence,
        FirstStartedRecordId = cell.FirstStartedRecordId,
        FirstStartedRecordSequence = cell.FirstStartedRecordSequence,
        Status = cell.Status,
        RequestCursor = request.Cursor,
        Limit = request.Limit,
        FieldsAvailability = availability,
        InputsAvailability = read.InputsAvailability,
        OutputsAvailability = read.OutputsAvailability,
        ErrorAvailability = read.ErrorAvailability,
        Fields = fields,
        NextCursor = nextCursor,
    };

    private static WorkflowRunCellFieldCursor CursorFor(WorkflowRunSelectedCell cell, FieldRow row) =>
        new(new WorkflowRunCellRecordIdentity(cell.StateRecordId, cell.StateRecordSequence, cell.FirstStartedRecordId, cell.FirstStartedRecordSequence),
            row.Section, row.Name!);

    private static bool CursorMatches(WorkflowRunCellFieldCursor cursor, WorkflowRunSelectedCell cell) =>
        cursor.Records.StateRecordId == cell.StateRecordId && cursor.Records.StateRecordSequence == cell.StateRecordSequence
        && cursor.Records.FirstStartedRecordId == cell.FirstStartedRecordId && cursor.Records.FirstStartedRecordSequence == cell.FirstStartedRecordSequence;

    private static bool ValidCoordinate(WorkflowRunCellFieldReadRequest request) => request.SourceRunId != Guid.Empty
        && ValidIdentity(request.NodeId, allowEmpty: false) && ValidIdentity(request.IterationKey, allowEmpty: true);

    private static bool ValidIdentity(string value, bool allowEmpty) => (allowEmpty || value.Length > 0)
        && value.Length <= WorkflowRunViewAdmissionService.MaximumIdentityCharacters * 2
        && value.EnumerateRunes().Count() <= WorkflowRunViewAdmissionService.MaximumIdentityCharacters;

    private static WorkflowRunCellFieldCursor? Validate(WorkflowRunCellFieldReadRequest request)
    {
        var errors = new List<string>();
        if (request.TeamId == Guid.Empty) errors.Add("TeamId must be non-empty.");
        if (request.RequestedRunId == Guid.Empty) errors.Add("RequestedRunId must be non-empty.");
        if (!Enum.IsDefined(request.Scope)) errors.Add("Scope must be a known Workflow Run view scope.");
        if (request.Limit is < 1 or > GetWorkflowRunCellFieldsQuery.MaximumPageSize)
            errors.Add($"Limit must be between 1 and {GetWorkflowRunCellFieldsQuery.MaximumPageSize}.");

        WorkflowRunCellFieldCursor? cursor = null;
        if (request.Cursor is not null)
        {
            if (WorkflowRunCellFieldCursor.TryDecode(request.Cursor, out var parsed) && ValidFieldName(parsed.Name)) cursor = parsed;
            else errors.Add("Cursor must be an opaque Workflow Run cell-field page cursor.");
        }
        if (errors.Count > 0) throw new WorkflowRunCellFieldReadRequestException(errors);
        return cursor;
    }

    private static bool ValidFieldName(string name) => name.EnumerateRunes().Count() <= MaximumFieldNameCharacters
        && Encoding.UTF8.GetByteCount(name) <= MaximumFieldNameUtf8Bytes;

    private static WorkflowRunCellFieldAvailability ParseAvailability(string value) =>
        Enum.TryParse<WorkflowRunCellFieldAvailability>(value, out var parsed) ? parsed : WorkflowRunCellFieldAvailability.Unavailable;

    private static WorkflowRunCellFieldJsonKind ParseKind(string value) => value switch
    {
        "object" => WorkflowRunCellFieldJsonKind.Object,
        "array" => WorkflowRunCellFieldJsonKind.Array,
        "string" => WorkflowRunCellFieldJsonKind.String,
        "number" => WorkflowRunCellFieldJsonKind.Number,
        "boolean" => WorkflowRunCellFieldJsonKind.Boolean,
        "null" => WorkflowRunCellFieldJsonKind.Null,
        _ => WorkflowRunCellFieldJsonKind.Unknown,
    };

    private static string? NullableString(DbDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    private static void Add(DbCommand command, string name, DbType type, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.DbType = type;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }

    private sealed record FieldRead(WorkflowRunCellFieldAvailability InputsAvailability,
        WorkflowRunCellFieldAvailability OutputsAvailability, WorkflowRunCellFieldAvailability ErrorAvailability,
        List<FieldRow> Rows);
    private sealed record FieldRow
    {
        public required WorkflowRunCellFieldSection Section { get; init; }
        public string? Name { get; init; }
        public required bool NameInvalid { get; init; }
        public required string JsonKind { get; init; }
        public required bool HasRefMarker { get; init; }
        public required bool CanonicalRef { get; init; }
        public string? RefId { get; init; }
        public string? DeclaredSize { get; init; }
        public string? DeclaredContentType { get; init; }
    }
    private sealed record ArtifactMetadataRow(Guid Id, long SizeBytes, string Sha256, bool HasExpectedContentType);
    private sealed record DescriptorOutcome(WorkflowRunCellFieldAvailability Availability, long? TotalBytes,
        string? Sha256, WorkflowRunCellFieldProblemCode? ProblemCode)
    {
        public static DescriptorOutcome AvailableInline { get; } = new(WorkflowRunCellFieldAvailability.Available, null, null, null);
        public static DescriptorOutcome AvailableArtifact(long totalBytes, string sha256) => new(WorkflowRunCellFieldAvailability.Available, totalBytes, sha256, null);
        public static DescriptorOutcome Failed(WorkflowRunCellFieldAvailability availability, WorkflowRunCellFieldProblemCode problemCode) => new(availability, null, null, problemCode);
    }
}
