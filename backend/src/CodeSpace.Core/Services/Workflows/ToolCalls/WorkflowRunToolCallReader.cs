using System.Data;
using System.Data.Common;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Services.Workflows.ToolCalls.Exceptions;
using CodeSpace.Messages.Contracts;
using CodeSpace.Messages.Dtos.Workflows.ToolCalls;
using CodeSpace.Messages.Queries.Workflows;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace CodeSpace.Core.Services.Workflows.ToolCalls;

/// <summary>
/// Bounded, metadata-only reader over the observation plane. The current producer covers terminal governed
/// side-effecting ledger calls, not every CLI tool invocation; native CLI activity remains in Agent Run events.
/// Raw string projection is deliberate: a future persisted enum string degrades to Corrupt instead of failing during
/// EF enum materialization before the wire decoder can describe the evidence honestly.
/// </summary>
public sealed class WorkflowRunToolCallReader : IWorkflowRunToolCallReader
{
    public const int MaximumAttempts = 100;

    internal const string PageSql = """
        SELECT call.id, call.workflow_run_id, call.tool_kind, call.tool_name, call.effect_class, call.state, call.call_ordinal,
               call.source_kind, call.source_correlation_id, call.capture_source, call.capture_completeness,
               call.created_at, call.last_modified_at, call.terminal_at, call.error_code
        FROM workflow_run_tool_call AS call
        INNER JOIN workflow_run AS run ON run.id = call.workflow_run_id
        WHERE run.team_id = @team_id
          AND call.workflow_run_id = @run_id
          AND (@has_cursor = FALSE
               OR call.created_at < @cursor_created_at
               OR (call.created_at = @cursor_created_at AND call.id < @cursor_id))
        ORDER BY call.created_at DESC, call.id DESC
        LIMIT @take
        """;

    internal const string DetailSql = """
        SELECT id, workflow_run_id, tool_kind, tool_name, effect_class, state, call_ordinal,
               source_kind, source_correlation_id, capture_source, capture_completeness,
               created_at, last_modified_at, terminal_at, error_code
        FROM workflow_run_tool_call
        WHERE team_id = @team_id
          AND workflow_run_id = @run_id
          AND id = @call_id
        LIMIT 1
        """;

    internal const string AttemptsSql = """
        SELECT attempt_ordinal, status, capture_source, capture_completeness,
               started_at, completed_at, created_at, last_modified_at, error_code
        FROM workflow_run_tool_call_attempt
        WHERE team_id = @team_id
          AND workflow_run_id = @run_id
          AND tool_call_id = @call_id
        ORDER BY attempt_ordinal
        LIMIT @take
        """;

    private readonly CodeSpaceDbContext _db;

    public WorkflowRunToolCallReader(CodeSpaceDbContext db) { _db = db; }

    public async Task<WorkflowRunToolCallPage?> ReadPageAsync(WorkflowRunToolCallPageRequest request, CancellationToken cancellationToken)
    {
        var cursor = Validate(request);
        var exists = await _db.WorkflowRun.AsNoTracking()
            .AnyAsync(value => value.Id == request.RunId && value.TeamId == request.TeamId, cancellationToken).ConfigureAwait(false);
        if (!exists) return null;

        var rows = await ReadPageRowsAsync(request, cursor, cancellationToken).ConfigureAwait(false);
        var hasMore = rows.Count > request.Limit;
        if (hasMore) rows.RemoveAt(rows.Count - 1);

        return new WorkflowRunToolCallPage
        {
            RunId = request.RunId,
            RequestCursor = request.Cursor,
            Limit = request.Limit,
            Items = rows,
            NextCursor = hasMore ? new WorkflowRunToolCallPageCursor(rows[^1].CreatedAt, rows[^1].ToolCallId).Encode() : null,
        };
    }

    public async Task<WorkflowRunToolCallDetail?> ReadDetailAsync(WorkflowRunToolCallDetailRequest request, CancellationToken cancellationToken)
    {
        Validate(request);
        var connection = _db.Database.GetDbConnection();
        var closeAfter = connection.State != ConnectionState.Open;
        if (closeAfter) await _db.Database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            var call = await ReadDetailRowAsync(connection, request, cancellationToken).ConfigureAwait(false);
            if (call is null) return null;

            var attempts = await ReadAttemptRowsAsync(connection, request, cancellationToken).ConfigureAwait(false);
            var truncated = attempts.Count > MaximumAttempts;
            if (truncated) attempts.RemoveAt(attempts.Count - 1);
            return new WorkflowRunToolCallDetail { Call = call, Attempts = attempts, AttemptsTruncated = truncated };
        }
        finally
        {
            if (closeAfter) await _db.Database.CloseConnectionAsync().ConfigureAwait(false);
        }
    }

    private async Task<List<WorkflowRunToolCallMetadata>> ReadPageRowsAsync(WorkflowRunToolCallPageRequest request, WorkflowRunToolCallPageCursor? cursor, CancellationToken cancellationToken)
    {
        var connection = _db.Database.GetDbConnection();
        var closeAfter = connection.State != ConnectionState.Open;
        if (closeAfter) await _db.Database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            await using var command = Command(connection, PageSql);
            Add(command, "team_id", DbType.Guid, request.TeamId);
            Add(command, "run_id", DbType.Guid, request.RunId);
            Add(command, "has_cursor", DbType.Boolean, cursor.HasValue);
            Add(command, "cursor_created_at", DbType.DateTimeOffset, cursor?.CreatedAt ?? DateTimeOffset.UnixEpoch);
            Add(command, "cursor_id", DbType.Guid, cursor?.Id ?? Guid.Empty);
            Add(command, "take", DbType.Int32, checked(request.Limit + 1));

            var rows = new List<WorkflowRunToolCallMetadata>(request.Limit + 1);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) rows.Add(WorkflowRunToolCallWire.Call(reader));
            return rows;
        }
        finally
        {
            if (closeAfter) await _db.Database.CloseConnectionAsync().ConfigureAwait(false);
        }
    }

    private async Task<WorkflowRunToolCallMetadata?> ReadDetailRowAsync(DbConnection connection, WorkflowRunToolCallDetailRequest request, CancellationToken cancellationToken)
    {
        await using var command = Command(connection, DetailSql);
        AddScope(command, request);
        await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SingleRow, cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? WorkflowRunToolCallWire.Call(reader) : null;
    }

    private async Task<List<WorkflowRunToolCallAttemptMetadata>> ReadAttemptRowsAsync(DbConnection connection, WorkflowRunToolCallDetailRequest request, CancellationToken cancellationToken)
    {
        await using var command = Command(connection, AttemptsSql);
        AddScope(command, request);
        Add(command, "take", DbType.Int32, MaximumAttempts + 1);
        var rows = new List<WorkflowRunToolCallAttemptMetadata>(MaximumAttempts + 1);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) rows.Add(WorkflowRunToolCallWire.Attempt(reader));
        return rows;
    }

    private DbCommand Command(DbConnection connection, string sql)
    {
        var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Transaction = _db.Database.CurrentTransaction?.GetDbTransaction();
        return command;
    }

    private static void AddScope(DbCommand command, WorkflowRunToolCallDetailRequest request)
    {
        Add(command, "team_id", DbType.Guid, request.TeamId);
        Add(command, "run_id", DbType.Guid, request.RunId);
        Add(command, "call_id", DbType.Guid, request.ToolCallId);
    }

    private static void Add(DbCommand command, string name, DbType type, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.DbType = type;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }

    private static WorkflowRunToolCallPageCursor? Validate(WorkflowRunToolCallPageRequest request)
    {
        var errors = new List<string>();
        if (request.TeamId == Guid.Empty) errors.Add("TeamId must be non-empty.");
        if (request.RunId == Guid.Empty) errors.Add("RunId must be non-empty.");
        if (request.Limit is < 1 or > ListWorkflowRunToolCallsQuery.MaximumPageSize)
            errors.Add($"Limit must be between 1 and {ListWorkflowRunToolCallsQuery.MaximumPageSize}.");
        WorkflowRunToolCallPageCursor? cursor = null;
        if (request.Cursor is not null)
        {
            if (WorkflowRunToolCallPageCursor.TryDecode(request.Cursor, out var parsed)) cursor = parsed;
            else errors.Add("Cursor must be an opaque Workflow Run tool-call page cursor.");
        }
        if (errors.Count > 0) throw new WorkflowRunToolCallReadRequestException(errors);
        return cursor;
    }

    private static void Validate(WorkflowRunToolCallDetailRequest request)
    {
        var errors = new List<string>();
        if (request.TeamId == Guid.Empty) errors.Add("TeamId must be non-empty.");
        if (request.RunId == Guid.Empty) errors.Add("RunId must be non-empty.");
        if (request.ToolCallId == Guid.Empty) errors.Add("ToolCallId must be non-empty.");
        if (errors.Count > 0) throw new WorkflowRunToolCallReadRequestException(errors);
    }
}

internal static class WorkflowRunToolCallWire
{
    internal static WorkflowRunToolCallMetadata Call(DbDataReader reader) => new()
    {
        ToolCallId = reader.GetGuid(0),
        RunId = reader.GetGuid(1),
        ToolAdapterKind = reader.GetString(2),
        ToolName = reader.GetString(3),
        EffectClass = DecodeEffect(NullableString(reader, 4)),
        State = DecodeState(NullableString(reader, 5)),
        CallOrdinal = reader.GetInt64(6),
        SourceKind = NullableString(reader, 7),
        SourceCorrelationId = NullableGuid(reader, 8),
        CaptureSource = reader.GetString(9),
        CaptureCompleteness = DecodeCapture(NullableString(reader, 10)),
        CreatedAt = reader.GetFieldValue<DateTimeOffset>(11),
        LastModifiedAt = reader.GetFieldValue<DateTimeOffset>(12),
        TerminalAt = NullableInstant(reader, 13),
        ErrorCode = DecodeErrorCode(NullableString(reader, 14)),
    };

    internal static WorkflowRunToolCallAttemptMetadata Attempt(DbDataReader reader) => new()
    {
        AttemptOrdinal = reader.GetInt32(0),
        Status = DecodeAttemptStatus(NullableString(reader, 1)),
        CaptureSource = reader.GetString(2),
        CaptureCompleteness = DecodeCapture(NullableString(reader, 3)),
        StartedAt = reader.GetFieldValue<DateTimeOffset>(4),
        CompletedAt = NullableInstant(reader, 5),
        CreatedAt = reader.GetFieldValue<DateTimeOffset>(6),
        LastModifiedAt = reader.GetFieldValue<DateTimeOffset>(7),
        ErrorCode = DecodeErrorCode(NullableString(reader, 8)),
    };

    internal static WorkflowRunToolCallEffectClass DecodeEffect(string? value) => value switch
    {
        "ReadOnly" => WorkflowRunToolCallEffectClass.ReadOnly,
        "SideEffecting" => WorkflowRunToolCallEffectClass.SideEffecting,
        "Unknown" => WorkflowRunToolCallEffectClass.Unknown,
        null or "" => WorkflowRunToolCallEffectClass.LegacyUnknown,
        _ when string.IsNullOrWhiteSpace(value) => WorkflowRunToolCallEffectClass.LegacyUnknown,
        _ => WorkflowRunToolCallEffectClass.Corrupt,
    };

    internal static WorkflowRunToolCallObservationState DecodeState(string? value) => value switch
    {
        "Pending" => WorkflowRunToolCallObservationState.Pending,
        "Running" => WorkflowRunToolCallObservationState.Running,
        "Completed" => WorkflowRunToolCallObservationState.Completed,
        "Abandoned" => WorkflowRunToolCallObservationState.Abandoned,
        null or "" => WorkflowRunToolCallObservationState.LegacyUnknown,
        _ when string.IsNullOrWhiteSpace(value) => WorkflowRunToolCallObservationState.LegacyUnknown,
        _ => WorkflowRunToolCallObservationState.Corrupt,
    };

    internal static WorkflowRunToolCallAttemptObservationStatus DecodeAttemptStatus(string? value) => value switch
    {
        "Pending" => WorkflowRunToolCallAttemptObservationStatus.Pending,
        "Running" => WorkflowRunToolCallAttemptObservationStatus.Running,
        "Succeeded" => WorkflowRunToolCallAttemptObservationStatus.Succeeded,
        "Failed" => WorkflowRunToolCallAttemptObservationStatus.Failed,
        "Denied" => WorkflowRunToolCallAttemptObservationStatus.Denied,
        "Cancelled" => WorkflowRunToolCallAttemptObservationStatus.Cancelled,
        "TimedOut" => WorkflowRunToolCallAttemptObservationStatus.TimedOut,
        "Indeterminate" => WorkflowRunToolCallAttemptObservationStatus.Indeterminate,
        null or "" => WorkflowRunToolCallAttemptObservationStatus.LegacyUnknown,
        _ when string.IsNullOrWhiteSpace(value) => WorkflowRunToolCallAttemptObservationStatus.LegacyUnknown,
        _ => WorkflowRunToolCallAttemptObservationStatus.Corrupt,
    };

    internal static WorkflowRunCaptureCompleteness DecodeCapture(string? value) => value switch
    {
        "Exact" => WorkflowRunCaptureCompleteness.Exact,
        "RedactedExact" => WorkflowRunCaptureCompleteness.RedactedExact,
        "Partial" => WorkflowRunCaptureCompleteness.Partial,
        "Unavailable" => WorkflowRunCaptureCompleteness.Unavailable,
        "Corrupt" => WorkflowRunCaptureCompleteness.Corrupt,
        "LegacyUnknown" => WorkflowRunCaptureCompleteness.LegacyUnknown,
        null or "" => WorkflowRunCaptureCompleteness.LegacyUnknown,
        _ when string.IsNullOrWhiteSpace(value) => WorkflowRunCaptureCompleteness.LegacyUnknown,
        _ => WorkflowRunCaptureCompleteness.Corrupt,
    };

    internal static WorkflowRunToolCallObservationErrorCode? DecodeErrorCode(string? value) => value switch
    {
        null => null,
        "ledger-failed-outcome-unknown" => WorkflowRunToolCallObservationErrorCode.LedgerFailedOutcomeUnknown,
        "governance-denied" => WorkflowRunToolCallObservationErrorCode.GovernanceDenied,
        "approval-expired" => WorkflowRunToolCallObservationErrorCode.ApprovalExpired,
        "" => WorkflowRunToolCallObservationErrorCode.LegacyUnknown,
        _ when string.IsNullOrWhiteSpace(value) => WorkflowRunToolCallObservationErrorCode.LegacyUnknown,
        _ => WorkflowRunToolCallObservationErrorCode.Corrupt,
    };

    private static string? NullableString(DbDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
    private static Guid? NullableGuid(DbDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetGuid(ordinal);
    private static DateTimeOffset? NullableInstant(DbDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetFieldValue<DateTimeOffset>(ordinal);
}
