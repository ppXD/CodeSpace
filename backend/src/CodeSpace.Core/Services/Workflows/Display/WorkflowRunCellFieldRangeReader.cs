using System.Buffers;
using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Text;
using System.Text.Json;
using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Services.Workflows.Artifacts;
using CodeSpace.Core.Services.Workflows.Display.Exceptions;
using CodeSpace.Messages.Dtos.Workflows;
using CodeSpace.Messages.Queries.Workflows;
using Microsoft.EntityFrameworkCore;

namespace CodeSpace.Core.Services.Workflows.Display;

/// <summary>
/// Observation-only reader for one exact cell field. Inline JSON is sliced inside PostgreSQL; artifact JSON delegates
/// to the recorded-revision range reader. Neither path exposes a storage locator or artifact identity.
/// </summary>
public sealed class WorkflowRunCellFieldRangeReader : IWorkflowRunCellFieldRangeReader, IScopedDependency
{
    public const int Utf8LookaheadBytes = 4;
    private const string JsonContentType = "application/json";

    internal const string InlineFieldSql = """
        WITH source AS MATERIALIZED (
            SELECT started.id IS NOT NULL AS first_record_present,
                   started.payload_json -> 'inputs' AS inputs_value,
                   state.payload_json -> 'outputs' AS outputs_value,
                   state.payload_json -> 'error' AS error_value
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
        ), selected AS MATERIALIZED (
            SELECT first_record_present,
                   CASE @section
                       WHEN 0 THEN @first_record_id IS NULL OR inputs_value IS NULL OR jsonb_typeof(inputs_value) = 'object'
                       WHEN 1 THEN outputs_value IS NULL OR jsonb_typeof(outputs_value) = 'object'
                       WHEN 2 THEN error_value IS NULL OR jsonb_typeof(error_value) IN ('null', 'string')
                       ELSE false END AS section_valid,
                   CASE @section
                       WHEN 0 THEN first_record_present AND jsonb_typeof(inputs_value) = 'object' AND inputs_value ? @field_name
                       WHEN 1 THEN jsonb_typeof(outputs_value) = 'object' AND outputs_value ? @field_name
                       WHEN 2 THEN error_value IS NOT NULL AND jsonb_typeof(error_value) = 'string'
                       ELSE false END AS field_present,
                   CASE @section
                       WHEN 0 THEN inputs_value -> @field_name
                       WHEN 1 THEN outputs_value -> @field_name
                       WHEN 2 THEN error_value
                       ELSE NULL END AS field_value
            FROM source
        ), assessed AS MATERIALIZED (
            SELECT first_record_present, section_valid, field_present, field_value,
                   coalesce(@section = 1 AND field_present AND jsonb_typeof(field_value) = 'object'
                       AND field_value ? '$artifact_ref', false) AS has_ref_marker,
                   CASE WHEN @section = 1 AND field_present AND jsonb_typeof(field_value) = 'object'
                                  AND field_value ? '$artifact_ref'
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
                   CASE WHEN @section = 1 AND jsonb_typeof(field_value) = 'object' AND field_value ? '$artifact_ref'
                                  AND char_length(field_value #>> '{$artifact_ref,id}') <= @max_ref_id_chars
                        THEN field_value #>> '{$artifact_ref,id}' END AS ref_id,
                   CASE WHEN @section = 1 AND jsonb_typeof(field_value) = 'object' AND field_value ? '$artifact_ref'
                                  AND char_length(field_value #>> '{$artifact_ref,size_bytes}') <= @max_declared_size_chars
                        THEN field_value #>> '{$artifact_ref,size_bytes}' END AS declared_size,
                   CASE WHEN @section = 1 AND jsonb_typeof(field_value) = 'object' AND field_value ? '$artifact_ref'
                                  AND char_length(field_value #>> '{$artifact_ref,content_type}') <= @max_content_type_chars
                        THEN field_value #>> '{$artifact_ref,content_type}' END AS declared_content_type
            FROM selected
        ), materialized AS MATERIALIZED (
            SELECT *, CASE WHEN field_present AND section_valid AND NOT has_ref_marker
                           THEN convert_to(field_value::text, 'UTF8') END AS inline_bytes
            FROM assessed
        )
        SELECT first_record_present, section_valid, field_present, has_ref_marker, canonical_ref,
               ref_id, declared_size, declared_content_type,
               octet_length(inline_bytes)::bigint AS total_bytes,
               CASE WHEN @offset <= octet_length(inline_bytes)::bigint
                    THEN substring(inline_bytes FROM (least(@offset, 2147483646)::integer + 1) FOR @take) END AS page_bytes
        FROM materialized
        """;

    private readonly CodeSpaceDbContext _db;
    private readonly IWorkflowRunViewAdmission _admission;
    private readonly IArtifactRangeReader _artifacts;

    public WorkflowRunCellFieldRangeReader(CodeSpaceDbContext db, IWorkflowRunViewAdmission admission, IArtifactRangeReader artifacts)
    {
        _db = db;
        _admission = admission;
        _artifacts = artifacts;
    }

    public async Task<WorkflowRunCellFieldRangePage?> ReadAsync(WorkflowRunCellFieldRangeReadRequest request, CancellationToken cancellationToken)
    {
        var cursor = Validate(request);
        var cell = await CurrentCellAsync(request, cancellationToken).ConfigureAwait(false);
        if (cell is null) return null;

        var offset = cursor?.OffsetBytes ?? 0;
        var identity = Identity(request);
        if (!RecordsMatch(request.Records, cell) || cursor is { } continuation && continuation.Identity != identity)
            return Failure(request, cell, new FailureOutcome(WorkflowRunCellFieldRangeAvailability.StaleIdentity, offset));

        var fact = await ReadFactAsync(request, offset, cancellationToken).ConfigureAwait(false);
        WorkflowRunCellFieldRangePage page;
        if (fact is null || request.Records.FirstStartedRecordId is not null && !fact.FirstRecordPresent)
            page = Failure(request, cell, new FailureOutcome(WorkflowRunCellFieldRangeAvailability.StaleIdentity, offset));
        else if (!fact.SectionValid)
            page = Failure(request, cell, new FailureOutcome(WorkflowRunCellFieldRangeAvailability.IntegrityFailure, offset));
        else if (!fact.FieldPresent)
            page = Failure(request, cell, new FailureOutcome(WorkflowRunCellFieldRangeAvailability.NotRecorded, offset));
        else if (fact.HasRefMarker)
            page = await ReadArtifactAsync(request, cell, fact, offset, cancellationToken).ConfigureAwait(false);
        else if (fact.TotalBytes is not { } total || fact.PageBytes is null)
            page = Failure(request, cell, new FailureOutcome(WorkflowRunCellFieldRangeAvailability.InvalidRange, offset,
                WorkflowRunCellFieldRangeSource.Inline, fact.TotalBytes, JsonContentType));
        else
            page = Utf8Page(request, cell, new PageContent(WorkflowRunCellFieldRangeSource.Inline, offset, fact.PageBytes, total,
                JsonContentType, IntegrityVerified: true));

        return await StillCurrentAsync(request, cancellationToken).ConfigureAwait(false)
            ? page
            : Failure(request, cell, new FailureOutcome(WorkflowRunCellFieldRangeAvailability.StaleIdentity, offset));
    }

    private async Task<WorkflowRunCellFieldRangePage> ReadArtifactAsync(WorkflowRunCellFieldRangeReadRequest request,
        WorkflowRunSelectedCell cell, InlineFact fact, long offset, CancellationToken cancellationToken)
    {
        if (!fact.CanonicalRef || !Guid.TryParse(fact.RefId, out var artifactId)
            || !long.TryParse(fact.DeclaredSize, NumberStyles.None, CultureInfo.InvariantCulture, out var declaredSize)
            || declaredSize < 0 || fact.DeclaredContentType != JsonContentType)
        {
            return Failure(request, cell, new FailureOutcome(WorkflowRunCellFieldRangeAvailability.CorruptReference, offset,
                WorkflowRunCellFieldRangeSource.Artifact, ContentType: JsonContentType));
        }

        var read = await _artifacts.ReadRangeAsync(request.TeamId, artifactId, offset,
            request.LimitBytes + Utf8LookaheadBytes, cancellationToken).ConfigureAwait(false);
        if (read.TotalLength is { } actualSize && actualSize != declaredSize
            || read.ContentType is { } actualType && actualType != fact.DeclaredContentType)
        {
            return Failure(request, cell, new FailureOutcome(WorkflowRunCellFieldRangeAvailability.CorruptReference, offset,
                WorkflowRunCellFieldRangeSource.Artifact, read.TotalLength, JsonContentType));
        }
        if (read.State != ArtifactRangeReadState.Available)
            return Failure(request, cell, new FailureOutcome(Map(read.State), offset, WorkflowRunCellFieldRangeSource.Artifact,
                read.TotalLength ?? declaredSize, JsonContentType));
        if (read.Bytes is null || read.TotalLength is null || read.ContentType is null)
            return Failure(request, cell, new FailureOutcome(WorkflowRunCellFieldRangeAvailability.IntegrityFailure, offset,
                WorkflowRunCellFieldRangeSource.Artifact, declaredSize, JsonContentType));

        return Utf8Page(request, cell, new PageContent(WorkflowRunCellFieldRangeSource.Artifact, offset, read.Bytes,
            read.TotalLength.Value, read.ContentType, read.IntegrityVerified));
    }

    private async Task<InlineFact?> ReadFactAsync(WorkflowRunCellFieldRangeReadRequest request, long offset,
        CancellationToken cancellationToken)
    {
        var connection = _db.Database.GetDbConnection();
        var closeAfter = connection.State != ConnectionState.Open;
        if (closeAfter) await _db.Database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = InlineFieldSql;
            Add(command, "source_run_id", DbType.Guid, request.SourceRunId);
            Add(command, "node_id", DbType.String, request.NodeId);
            Add(command, "iteration_key", DbType.String, request.IterationKey);
            Add(command, "state_record_id", DbType.Guid, request.Records.StateRecordId);
            Add(command, "state_record_sequence", DbType.Int64, request.Records.StateRecordSequence);
            Add(command, "first_record_id", DbType.Guid, request.Records.FirstStartedRecordId is { } firstId ? firstId : DBNull.Value);
            Add(command, "first_record_sequence", DbType.Int64, request.Records.FirstStartedRecordSequence is { } firstSequence ? firstSequence : DBNull.Value);
            Add(command, "section", DbType.Int32, (int)request.Section);
            Add(command, "field_name", DbType.String, request.Name ?? (object)DBNull.Value);
            Add(command, "max_ref_id_chars", DbType.Int32, WorkflowRunCellFieldReader.MaximumArtifactIdCharacters);
            Add(command, "max_declared_size_chars", DbType.Int32, WorkflowRunCellFieldReader.MaximumDeclaredSizeCharacters);
            Add(command, "max_content_type_chars", DbType.Int32, WorkflowRunCellFieldReader.MaximumContentTypeCharacters);
            Add(command, "offset", DbType.Int64, offset);
            Add(command, "take", DbType.Int32, request.LimitBytes + Utf8LookaheadBytes);

            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) return null;
            return new InlineFact
            {
                FirstRecordPresent = reader.GetBoolean(0),
                SectionValid = reader.GetBoolean(1),
                FieldPresent = reader.GetBoolean(2),
                HasRefMarker = reader.GetBoolean(3),
                CanonicalRef = reader.GetBoolean(4),
                RefId = NullableString(reader, 5),
                DeclaredSize = NullableString(reader, 6),
                DeclaredContentType = NullableString(reader, 7),
                TotalBytes = NullableInt64(reader, 8),
                PageBytes = reader.IsDBNull(9) ? null : reader.GetFieldValue<byte[]>(9),
            };
        }
        finally
        {
            if (closeAfter) await _db.Database.CloseConnectionAsync().ConfigureAwait(false);
        }
    }

    private async Task<WorkflowRunSelectedCell?> CurrentCellAsync(WorkflowRunCellFieldRangeReadRequest request,
        CancellationToken cancellationToken)
    {
        var admitted = await _admission.AdmitAsync(request.RequestedRunId, request.TeamId, request.Scope, cancellationToken).ConfigureAwait(false);
        if (admitted is null || admitted.LineageAvailability != WorkflowRunViewAvailability.Available) return null;
        var cells = await _admission.ReadSelectedCellsAsync(admitted,
            new WorkflowRunCellCoordinate(request.NodeId, request.IterationKey), take: 2, cancellationToken).ConfigureAwait(false);
        if (cells.Count != 1) return null;
        var cell = cells[0];
        return !cell.IdentityInvalid && cell.SourceRunId == request.SourceRunId && cell.NodeId == request.NodeId
            && cell.IterationKey == request.IterationKey ? cell : null;
    }

    private async Task<bool> StillCurrentAsync(WorkflowRunCellFieldRangeReadRequest request, CancellationToken cancellationToken)
    {
        var current = await CurrentCellAsync(request, cancellationToken).ConfigureAwait(false);
        return current is not null && RecordsMatch(request.Records, current);
    }

    private static WorkflowRunCellFieldRangePage Utf8Page(WorkflowRunCellFieldRangeReadRequest request,
        WorkflowRunSelectedCell cell, PageContent content)
    {
        var (source, offset, bytes, totalBytes, contentType, integrityVerified) = content;
        if (totalBytes < 0 || offset > totalBytes)
            return Failure(request, cell, new FailureOutcome(WorkflowRunCellFieldRangeAvailability.InvalidRange, offset, source, totalBytes, contentType));
        if (bytes.LongLength > totalBytes - offset || bytes.Length > request.LimitBytes + Utf8LookaheadBytes)
            return Failure(request, cell, new FailureOutcome(WorkflowRunCellFieldRangeAvailability.IntegrityFailure, offset, source, totalBytes, contentType));
        if (bytes.Length > 0 && IsContinuation(bytes[0]))
            return Failure(request, cell, new FailureOutcome(WorkflowRunCellFieldRangeAvailability.InvalidRange, offset, source, totalBytes, contentType));

        var consumed = 0;
        var max = Math.Min(request.LimitBytes, bytes.Length);
        while (consumed < max)
        {
            var status = Rune.DecodeFromUtf8(bytes.AsSpan(consumed), out _, out var runeBytes);
            if (status == OperationStatus.Done && consumed + runeBytes <= max)
            {
                consumed += runeBytes;
                continue;
            }
            if (status == OperationStatus.Done)
            {
                if (consumed == 0)
                    return Failure(request, cell, new FailureOutcome(WorkflowRunCellFieldRangeAvailability.InvalidRange, offset, source, totalBytes, contentType));
                break;
            }
            return Failure(request, cell, new FailureOutcome(WorkflowRunCellFieldRangeAvailability.IntegrityFailure, offset, source, totalBytes, contentType));
        }

        if (consumed == 0 && offset < totalBytes)
            return Failure(request, cell, new FailureOutcome(WorkflowRunCellFieldRangeAvailability.IntegrityFailure, offset, source, totalBytes, contentType));
        var nextOffset = offset + consumed;
        var next = nextOffset < totalBytes ? new WorkflowRunCellFieldRangeCursor(Identity(request), nextOffset).Encode() : null;
        var completeJsonValue = offset == 0 && next is null;
        if (completeJsonValue && !IsJsonValue(bytes.AsMemory(0, consumed)))
            return Failure(request, cell, new FailureOutcome(WorkflowRunCellFieldRangeAvailability.IntegrityFailure, offset, source, totalBytes, contentType));
        return Base(request, cell, offset) with
        {
            Availability = WorkflowRunCellFieldRangeAvailability.Available,
            Source = source,
            ReturnedBytes = consumed,
            TotalBytes = totalBytes,
            NextCursor = next,
            Text = Encoding.UTF8.GetString(bytes, 0, consumed),
            ContentType = contentType,
            IntegrityVerified = integrityVerified,
            CompleteJsonValue = completeJsonValue,
            Retryable = false,
        };
    }

    private static WorkflowRunCellFieldRangePage Failure(WorkflowRunCellFieldRangeReadRequest request,
        WorkflowRunSelectedCell cell, FailureOutcome failure) => Base(request, cell, failure.OffsetBytes) with
    {
        Availability = failure.Availability,
        Source = failure.Source,
        ReturnedBytes = 0,
        TotalBytes = failure.TotalBytes,
        ContentType = failure.ContentType,
        IntegrityVerified = false,
        CompleteJsonValue = false,
        Retryable = failure.Availability == WorkflowRunCellFieldRangeAvailability.BackendUnavailable,
    };

    private static WorkflowRunCellFieldRangePage Base(WorkflowRunCellFieldRangeReadRequest request,
        WorkflowRunSelectedCell cell, long offset) => new()
    {
        RequestedRunId = request.RequestedRunId,
        Scope = request.Scope,
        SourceRunId = request.SourceRunId,
        NodeId = request.NodeId,
        IterationKey = request.IterationKey,
        StateRecordId = request.Records.StateRecordId,
        StateRecordSequence = request.Records.StateRecordSequence,
        FirstStartedRecordId = request.Records.FirstStartedRecordId,
        FirstStartedRecordSequence = request.Records.FirstStartedRecordSequence,
        Status = cell.Status,
        Section = request.Section,
        Name = request.Name,
        Availability = WorkflowRunCellFieldRangeAvailability.IntegrityFailure,
        Source = WorkflowRunCellFieldRangeSource.Unavailable,
        RequestCursor = request.Cursor,
        LimitBytes = request.LimitBytes,
        OffsetBytes = offset,
        ReturnedBytes = 0,
        IntegrityVerified = false,
        CompleteJsonValue = false,
        Retryable = false,
    };

    private static WorkflowRunCellFieldRangeCursor? Validate(WorkflowRunCellFieldRangeReadRequest request)
    {
        var errors = new List<string>();
        if (request.TeamId == Guid.Empty) errors.Add("TeamId must be non-empty.");
        if (request.RequestedRunId == Guid.Empty) errors.Add("RequestedRunId must be non-empty.");
        if (!Enum.IsDefined(request.Scope)) errors.Add("Scope must be a known Workflow Run view scope.");
        if (request.SourceRunId == Guid.Empty) errors.Add("SourceRunId must be non-empty.");
        if (!ValidIdentity(request.NodeId, allowEmpty: false) || !ValidIdentity(request.IterationKey, allowEmpty: true))
            errors.Add("Cell coordinate is invalid.");
        if (request.Records.StateRecordId == Guid.Empty || request.Records.StateRecordSequence <= 0)
            errors.Add("State record identity is invalid.");
        if ((request.Records.FirstStartedRecordId is null) != (request.Records.FirstStartedRecordSequence is null)
            || request.Records.FirstStartedRecordId is { } firstId && firstId == Guid.Empty
            || request.Records.FirstStartedRecordSequence is { } firstSequence && firstSequence <= 0)
            errors.Add("First-start record identity is invalid.");
        if (!Enum.IsDefined(request.Section)) errors.Add("Section must be known.");
        if (request.Section == WorkflowRunCellFieldSection.Error ? request.Name is not null : request.Name is null || !ValidFieldName(request.Name))
            errors.Add("Field name does not match its section.");
        if (request.LimitBytes is < 1 or > ReadWorkflowRunCellFieldRangeQuery.MaximumPageBytes)
            errors.Add($"LimitBytes must be between 1 and {ReadWorkflowRunCellFieldRangeQuery.MaximumPageBytes}.");

        WorkflowRunCellFieldRangeCursor? cursor = null;
        if (request.Cursor is not null)
        {
            if (WorkflowRunCellFieldRangeCursor.TryDecode(request.Cursor, out var parsed) && parsed.OffsetBytes >= 0) cursor = parsed;
            else errors.Add("Cursor must be an opaque Workflow Run cell-field range cursor.");
        }
        if (errors.Count > 0) throw new WorkflowRunCellFieldReadRequestException(errors);
        return cursor;
    }

    private static WorkflowRunCellFieldRangeIdentity Identity(WorkflowRunCellFieldRangeReadRequest request) => new()
    {
        RequestedRunId = request.RequestedRunId,
        Scope = request.Scope,
        SourceRunId = request.SourceRunId,
        NodeId = request.NodeId,
        IterationKey = request.IterationKey,
        Records = request.Records,
        Section = request.Section,
        Name = request.Name,
    };

    private static bool RecordsMatch(WorkflowRunCellRecordIdentity records, WorkflowRunSelectedCell cell) =>
        records.StateRecordId == cell.StateRecordId && records.StateRecordSequence == cell.StateRecordSequence
        && records.FirstStartedRecordId == cell.FirstStartedRecordId
        && records.FirstStartedRecordSequence == cell.FirstStartedRecordSequence;

    private static bool ValidIdentity(string value, bool allowEmpty) => (allowEmpty || value.Length > 0)
        && value.Length <= WorkflowRunViewAdmissionService.MaximumIdentityCharacters * 2
        && value.EnumerateRunes().Count() <= WorkflowRunViewAdmissionService.MaximumIdentityCharacters;

    private static bool ValidFieldName(string value) => value.EnumerateRunes().Count() <= WorkflowRunCellFieldReader.MaximumFieldNameCharacters
        && Encoding.UTF8.GetByteCount(value) <= WorkflowRunCellFieldReader.MaximumFieldNameUtf8Bytes;

    private static bool IsContinuation(byte value) => (value & 0b1100_0000) == 0b1000_0000;

    private static bool IsJsonValue(ReadOnlyMemory<byte> bytes)
    {
        try
        {
            using var document = JsonDocument.Parse(bytes);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static WorkflowRunCellFieldRangeAvailability Map(ArtifactRangeReadState state) => state switch
    {
        ArtifactRangeReadState.MetadataMissing => WorkflowRunCellFieldRangeAvailability.MetadataMissing,
        ArtifactRangeReadState.PhysicalObjectMissing => WorkflowRunCellFieldRangeAvailability.PhysicalObjectMissing,
        ArtifactRangeReadState.IntegrityFailure => WorkflowRunCellFieldRangeAvailability.IntegrityFailure,
        ArtifactRangeReadState.BackendUnavailable => WorkflowRunCellFieldRangeAvailability.BackendUnavailable,
        ArtifactRangeReadState.AccessDenied => WorkflowRunCellFieldRangeAvailability.AccessDenied,
        ArtifactRangeReadState.InvalidOffset => WorkflowRunCellFieldRangeAvailability.InvalidRange,
        _ => WorkflowRunCellFieldRangeAvailability.IntegrityFailure,
    };

    private static string? NullableString(DbDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
    private static long? NullableInt64(DbDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetInt64(ordinal);

    private static void Add(DbCommand command, string name, DbType type, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.DbType = type;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }

    private sealed record InlineFact
    {
        public required bool FirstRecordPresent { get; init; }
        public required bool SectionValid { get; init; }
        public required bool FieldPresent { get; init; }
        public required bool HasRefMarker { get; init; }
        public required bool CanonicalRef { get; init; }
        public string? RefId { get; init; }
        public string? DeclaredSize { get; init; }
        public string? DeclaredContentType { get; init; }
        public long? TotalBytes { get; init; }
        public byte[]? PageBytes { get; init; }
    }

    private sealed record PageContent(WorkflowRunCellFieldRangeSource Source, long OffsetBytes, byte[] Bytes,
        long TotalBytes, string ContentType, bool IntegrityVerified);

    private sealed record FailureOutcome(WorkflowRunCellFieldRangeAvailability Availability, long OffsetBytes,
        WorkflowRunCellFieldRangeSource Source = WorkflowRunCellFieldRangeSource.Unavailable, long? TotalBytes = null,
        string? ContentType = null);
}
