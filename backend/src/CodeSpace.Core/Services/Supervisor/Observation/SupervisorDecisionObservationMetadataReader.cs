using System.Data;
using System.Data.Common;
using System.Diagnostics;
using System.Text;
using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Services.Supervisor.Observation.Exceptions;
using CodeSpace.Messages.Dtos.Workflows.Supervisor;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace CodeSpace.Core.Services.Supervisor.Observation;

/// <summary>
/// Raw-ADO, exact-team/run, metadata-only keyset reader. Every call uses one short repeatable-read snapshot when it
/// owns the transaction: ownership, cursor heads and the bounded page cannot observe contradictory commits. The row
/// SQL never names or materializes payload/outcome/envelope/body columns. This is an internal foundation only; no
/// production observation consumer has cut over from #1615's full request bundle yet.
/// </summary>
public sealed class SupervisorDecisionObservationMetadataReader : ISupervisorDecisionObservationMetadataReader, IScopedDependency
{
    public const int ErrorPrefixMaximumChars = 400;

    private const string MetadataColumns = """
        decision.id, decision.supervisor_run_id, decision.decision_kind, decision.status,
        decision.story_order, decision.observation_revision, decision.created_date, decision.last_modified_date,
        LEFT(decision.error, @error_chars) AS error_prefix, COALESCE(OCTET_LENGTH(decision.error), 0) AS error_total_bytes
        """;

    internal const string TailSql = "SELECT\n" + MetadataColumns + """

        FROM supervisor_decision AS decision
        WHERE decision.team_id = @team_id
          AND decision.supervisor_run_id = @run_id
        ORDER BY decision.story_order DESC
        LIMIT @take
        """;

    internal const string OlderSql = "SELECT\n" + MetadataColumns + """

        FROM supervisor_decision AS decision
        WHERE decision.team_id = @team_id
          AND decision.supervisor_run_id = @run_id
          AND decision.story_order < @cursor
        ORDER BY decision.story_order DESC
        LIMIT @take
        """;

    internal const string NewerSql = "SELECT\n" + MetadataColumns + """

        FROM supervisor_decision AS decision
        WHERE decision.team_id = @team_id
          AND decision.supervisor_run_id = @run_id
          AND decision.story_order > @cursor
        ORDER BY decision.story_order ASC
        LIMIT @take
        """;

    internal const string ChangesSql = "SELECT\n" + MetadataColumns + """

        FROM supervisor_decision AS decision
        WHERE decision.team_id = @team_id
          AND decision.supervisor_run_id = @run_id
          AND decision.observation_revision > @cursor
        ORDER BY decision.observation_revision ASC
        LIMIT @take
        """;

    internal const string OwnershipSql = """
        SELECT EXISTS (
            SELECT 1
            FROM workflow_run
            WHERE id = @run_id
              AND team_id = @team_id
        )
        """;

    internal const string HeadsSql = """
        SELECT
            COALESCE((
                SELECT story_order
                FROM supervisor_decision
                WHERE team_id = @team_id AND supervisor_run_id = @run_id
                ORDER BY story_order DESC
                LIMIT 1
            ), 0),
            COALESCE((
                SELECT observation_revision
                FROM supervisor_decision
                WHERE team_id = @team_id AND supervisor_run_id = @run_id
                ORDER BY observation_revision DESC
                LIMIT 1
            ), 0)
        """;

    private readonly CodeSpaceDbContext _db;

    public SupervisorDecisionObservationMetadataReader(CodeSpaceDbContext db) { _db = db; }

    public async Task<SupervisorDecisionObservationStoryPage?> ReadStoryPageAsync(SupervisorDecisionObservationStoryPageRequest request, CancellationToken cancellationToken)
    {
        var cursor = Validate(request);
        return await InSnapshotAsync(async (connection, token) =>
        {
            if (!await IsOwnedAsync(connection, request.TeamId, request.SupervisorRunId, token).ConfigureAwait(false)) return null;

            var heads = await ReadHeadsAsync(connection, request.TeamId, request.SupervisorRunId, token).ConfigureAwait(false);
            var changeFeedWatermark = cursor?.SnapshotRevision ?? heads.ObservationRevision;
            var rows = await ReadRowsAsync(connection, StorySql(request.Mode), request.TeamId, request.SupervisorRunId, cursor?.StoryOrder ?? 0, checked(request.Limit + 1), token).ConfigureAwait(false);
            var hasMore = rows.Count > request.Limit;
            if (hasMore) rows.RemoveAt(rows.Count - 1);
            if (request.Mode != SupervisorDecisionObservationStoryPageMode.Newer) rows.Reverse();

            var nextNewerOrder = rows.Count > 0 ? rows[^1].StoryOrder : cursor?.StoryOrder ?? heads.StoryOrder;
            return new SupervisorDecisionObservationStoryPage
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
                    ? new SupervisorDecisionObservationStoryCursor(request.TeamId, request.SupervisorRunId, rows[0].StoryOrder, changeFeedWatermark).Encode()
                    : null,
                NextNewerCursor = new SupervisorDecisionObservationStoryCursor(request.TeamId, request.SupervisorRunId, nextNewerOrder, changeFeedWatermark).Encode(),
            };
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<SupervisorDecisionObservationChangePage?> ReadChangesAsync(SupervisorDecisionObservationChangePageRequest request, CancellationToken cancellationToken)
    {
        var cursor = Validate(request);
        var afterRevision = cursor?.ObservationRevision ?? 0;
        return await InSnapshotAsync(async (connection, token) =>
        {
            if (!await IsOwnedAsync(connection, request.TeamId, request.SupervisorRunId, token).ConfigureAwait(false)) return null;

            var heads = await ReadHeadsAsync(connection, request.TeamId, request.SupervisorRunId, token).ConfigureAwait(false);
            var rows = await ReadRowsAsync(connection, ChangesSql, request.TeamId, request.SupervisorRunId, afterRevision, checked(request.Limit + 1), token).ConfigureAwait(false);
            var hasMore = rows.Count > request.Limit;
            if (hasMore) rows.RemoveAt(rows.Count - 1);
            var nextRevision = rows.Count > 0 ? rows[^1].ObservationRevision : afterRevision;

            return new SupervisorDecisionObservationChangePage
            {
                SupervisorRunId = request.SupervisorRunId,
                RequestCursor = request.AfterCursor,
                Limit = request.Limit,
                SnapshotRevision = afterRevision,
                HeadRevision = heads.ObservationRevision,
                Items = rows,
                HasMore = hasMore,
                NextCursor = new SupervisorDecisionObservationChangeCursor(request.TeamId, request.SupervisorRunId, nextRevision).Encode(),
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
        await using var command = Command(connection, OwnershipSql);
        AddScope(command, teamId, supervisorRunId);
        return (bool)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
    }

    private async Task<ObservationHeads> ReadHeadsAsync(DbConnection connection, Guid teamId, Guid supervisorRunId, CancellationToken cancellationToken)
    {
        await using var command = Command(connection, HeadsSql);
        AddScope(command, teamId, supervisorRunId);
        await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SingleRow, cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) throw new InvalidOperationException("Supervisor observation heads query returned no row.");
        return new ObservationHeads(reader.GetInt64(0), reader.GetInt64(1));
    }

    private async Task<List<SupervisorDecisionObservationMetadata>> ReadRowsAsync(DbConnection connection, string sql, Guid teamId, Guid supervisorRunId, long cursor, int take, CancellationToken cancellationToken)
    {
        await using var command = Command(connection, sql);
        AddScope(command, teamId, supervisorRunId);
        Add(command, "cursor", DbType.Int64, cursor);
        Add(command, "take", DbType.Int32, take);
        Add(command, "error_chars", DbType.Int32, ErrorPrefixMaximumChars);
        var rows = new List<SupervisorDecisionObservationMetadata>(take);
        await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess, cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) rows.Add(SupervisorDecisionObservationWire.Read(reader));
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

    private static string StorySql(SupervisorDecisionObservationStoryPageMode mode) => mode switch
    {
        SupervisorDecisionObservationStoryPageMode.Tail => TailSql,
        SupervisorDecisionObservationStoryPageMode.Older => OlderSql,
        SupervisorDecisionObservationStoryPageMode.Newer => NewerSql,
        _ => throw new UnreachableException(),
    };

    private static SupervisorDecisionObservationStoryCursor? Validate(SupervisorDecisionObservationStoryPageRequest request)
    {
        request.ValidateShape();
        if (request.Cursor is null) return null;
        if (!SupervisorDecisionObservationStoryCursor.TryDecode(request.Cursor, request.TeamId, request.SupervisorRunId, out var cursor))
            throw new SupervisorDecisionObservationReadRequestException(["Cursor must be an opaque v1 story cursor for this exact team and SupervisorRun."]);
        if (request.Mode == SupervisorDecisionObservationStoryPageMode.Older && cursor.StoryOrder == 0)
            throw new SupervisorDecisionObservationReadRequestException(["Older requires a positive story cursor."]);
        return cursor;
    }

    private static SupervisorDecisionObservationChangeCursor? Validate(SupervisorDecisionObservationChangePageRequest request)
    {
        request.ValidateShape();
        if (request.AfterCursor is null) return null;
        if (!SupervisorDecisionObservationChangeCursor.TryDecode(request.AfterCursor, request.TeamId, request.SupervisorRunId, out var cursor))
            throw new SupervisorDecisionObservationReadRequestException(["AfterCursor must be an opaque v1 change cursor for this exact team and SupervisorRun."]);
        return cursor;
    }

    private readonly record struct ObservationHeads(long StoryOrder, long ObservationRevision);
}

internal static class SupervisorDecisionObservationWire
{
    internal static SupervisorDecisionObservationMetadata Read(DbDataReader reader)
    {
        var decisionId = reader.GetGuid(0);
        var supervisorRunId = reader.GetGuid(1);
        var decisionKind = reader.GetString(2);
        var status = DecodeStatus(NullableString(reader, 3));
        var storyOrder = reader.GetInt64(4);
        var observationRevision = reader.GetInt64(5);
        var createdAt = reader.GetFieldValue<DateTimeOffset>(6);
        var lastModifiedAt = reader.GetFieldValue<DateTimeOffset>(7);
        var errorPrefix = NullableString(reader, 8);
        var errorTotalBytes = reader.GetInt32(9);
        return new SupervisorDecisionObservationMetadata
        {
            DecisionId = decisionId,
            SupervisorRunId = supervisorRunId,
            DecisionKind = decisionKind,
            Status = status,
            StoryOrder = storyOrder,
            ObservationRevision = observationRevision,
            CreatedAt = createdAt,
            LastModifiedAt = lastModifiedAt,
            ErrorPrefix = errorPrefix,
            ErrorTotalBytes = errorTotalBytes,
            ErrorState = DecodeError(errorPrefix, errorTotalBytes),
        };
    }

    internal static SupervisorDecisionObservationStatus DecodeStatus(string? value) => value switch
    {
        "Pending" => SupervisorDecisionObservationStatus.Pending,
        "AwaitingApproval" => SupervisorDecisionObservationStatus.AwaitingApproval,
        "Running" => SupervisorDecisionObservationStatus.Running,
        "Succeeded" => SupervisorDecisionObservationStatus.Succeeded,
        "Failed" => SupervisorDecisionObservationStatus.Failed,
        "Expired" => SupervisorDecisionObservationStatus.Expired,
        null or "" => SupervisorDecisionObservationStatus.LegacyUnknown,
        _ => SupervisorDecisionObservationStatus.Corrupt,
    };

    internal static SupervisorDecisionObservationErrorState DecodeError(string? prefix, int totalBytes)
    {
        if (totalBytes < 0) return SupervisorDecisionObservationErrorState.Corrupt;
        if (prefix is null) return totalBytes == 0 ? SupervisorDecisionObservationErrorState.None : SupervisorDecisionObservationErrorState.Corrupt;
        var prefixBytes = Encoding.UTF8.GetByteCount(prefix);
        if (prefixBytes > totalBytes) return SupervisorDecisionObservationErrorState.Corrupt;
        return prefixBytes == totalBytes ? SupervisorDecisionObservationErrorState.Complete : SupervisorDecisionObservationErrorState.Truncated;
    }

    private static string? NullableString(DbDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
}
