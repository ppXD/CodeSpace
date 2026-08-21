using System.Data;
using System.Data.Common;
using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Messages.Queries.Tasks;
using CodeSpace.Messages.Tasks.Trace;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace CodeSpace.Core.Services.Tasks.Trace;

/// <summary>
/// Reads the PostgreSQL JSONB representation as UTF-8 bytes. The metadata admission deliberately precedes range
/// validation so a foreign/wrong-run record stays indistinguishable from an absent record, while invalid ranges are
/// rejected before the JSONB column is touched. The body query repeats every identity predicate as a defence in depth.
/// </summary>
public sealed class RunRecordPayloadReader : IRunRecordPayloadReader, IScopedDependency
{
    internal const string RangeSql = """
        WITH source AS MATERIALIZED (
            SELECT record.sequence, convert_to(record.payload_json::text, 'UTF8') AS payload_bytes
              FROM workflow_run_record AS record
              JOIN workflow_run AS run ON run.id = record.run_id
             WHERE run.team_id = @team_id
               AND record.run_id = @run_id
               AND record.id = @record_id
             LIMIT 1
        )
        SELECT sequence,
               octet_length(payload_bytes) AS total_bytes,
               substring(payload_bytes FROM CAST(@offset_bytes + 1 AS integer) FOR @limit_bytes) AS content
          FROM source
        """;

    private readonly CodeSpaceDbContext _db;

    public RunRecordPayloadReader(CodeSpaceDbContext db) { _db = db; }

    public async Task<RunRecordPayloadRangeRead?> ReadAsync(RunRecordPayloadRangeRequest request, CancellationToken cancellationToken)
    {
        var sequence = await SourceQuery(_db, request).SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (sequence == null) return null;

        var query = new ReadRunRecordPayloadRangeQuery
        {
            RunId = request.RunId, RecordId = request.RecordId, OffsetBytes = request.OffsetBytes, LimitBytes = request.LimitBytes,
        };
        if (!RunRecordPayloadWire.ValidRange(query)) return RunRecordPayloadWire.Unavailable(query, sequence.Value);

        var raw = await ReadRangeAsync(request, cancellationToken).ConfigureAwait(false);
        if (raw == null) return null;
        return request.OffsetBytes > raw.TotalBytes
            ? RunRecordPayloadWire.Unavailable(query, raw.Sequence, raw.TotalBytes)
            : RunRecordPayloadWire.Available(query, raw.Sequence, raw.TotalBytes, raw.Content);
    }

    internal static IQueryable<long?> SourceQuery(CodeSpaceDbContext db, RunRecordPayloadRangeRequest request) =>
        db.WorkflowRunRecord.AsNoTracking()
            .Where(record => record.Id == request.RecordId && record.RunId == request.RunId && record.Run.TeamId == request.TeamId)
            .Select(record => (long?)record.Sequence);

    private async Task<RawRange?> ReadRangeAsync(RunRecordPayloadRangeRequest request, CancellationToken cancellationToken)
    {
        var connection = _db.Database.GetDbConnection();
        var closeAfter = connection.State != ConnectionState.Open;
        if (closeAfter) await _db.Database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            await using var command = connection.CreateCommand();
            command.Transaction = _db.Database.CurrentTransaction?.GetDbTransaction();
            command.CommandText = RangeSql;
            command.Parameters.Add(Parameter(command, "team_id", DbType.Guid, request.TeamId));
            command.Parameters.Add(Parameter(command, "run_id", DbType.Guid, request.RunId));
            command.Parameters.Add(Parameter(command, "record_id", DbType.Guid, request.RecordId));
            command.Parameters.Add(Parameter(command, "offset_bytes", DbType.Int64, request.OffsetBytes));
            command.Parameters.Add(Parameter(command, "limit_bytes", DbType.Int32, request.LimitBytes));

            await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SingleRow, cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) return null;
            return new RawRange(reader.GetInt64(0), reader.GetInt32(1), reader.GetFieldValue<byte[]>(2));
        }
        finally
        {
            if (closeAfter) await _db.Database.CloseConnectionAsync().ConfigureAwait(false);
        }
    }

    private static DbParameter Parameter(DbCommand command, string name, DbType type, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.DbType = type;
        parameter.Value = value;
        return parameter;
    }

    private sealed record RawRange(long Sequence, long TotalBytes, byte[] Content);
}

internal static class RunRecordPayloadWire
{
    internal const int MaximumRangeBytes = 64 * 1024;

    internal static bool ValidRange(ReadRunRecordPayloadRangeQuery request) => request.OffsetBytes >= 0
        && request.LimitBytes is > 0 and <= MaximumRangeBytes
        && request.OffsetBytes <= int.MaxValue - request.LimitBytes;

    internal static RunRecordPayloadRangeRead Available(ReadRunRecordPayloadRangeQuery request, long sequence, long totalBytes, byte[] content)
    {
        if (content.Length > request.LimitBytes) throw new InvalidOperationException("Record payload reader returned more bytes than requested.");
        var next = checked(request.OffsetBytes + content.LongLength);
        if (next > totalBytes || (content.Length == 0 && request.OffsetBytes < totalBytes))
            throw new InvalidOperationException("Record payload reader returned a contradictory byte range.");

        return new RunRecordPayloadRangeRead
        {
            RunId = request.RunId,
            RecordId = request.RecordId,
            Sequence = sequence,
            Availability = RunRecordPayloadReadAvailability.Available,
            OffsetBytes = request.OffsetBytes,
            ReturnedBytes = content.Length,
            TotalBytes = totalBytes,
            NextOffsetBytes = next < totalBytes ? next : null,
            ContentType = "application/json",
            IsRetryable = false,
            Content = content,
        };
    }

    internal static RunRecordPayloadRangeRead Unavailable(ReadRunRecordPayloadRangeQuery request, long sequence, long? totalBytes = null) => new()
    {
        RunId = request.RunId,
        RecordId = request.RecordId,
        Sequence = sequence,
        Availability = RunRecordPayloadReadAvailability.InvalidRange,
        OffsetBytes = request.OffsetBytes,
        ReturnedBytes = 0,
        TotalBytes = totalBytes,
        NextOffsetBytes = null,
        ContentType = "application/json",
        IsRetryable = false,
        ProblemCode = nameof(RunRecordPayloadReadAvailability.InvalidRange),
    };
}
