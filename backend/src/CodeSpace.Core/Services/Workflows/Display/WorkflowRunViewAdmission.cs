using System.Data;
using System.Data.Common;
using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Dtos.Workflows;
using CodeSpace.Messages.Enums;
using Microsoft.EntityFrameworkCore;

namespace CodeSpace.Core.Services.Workflows.Display;

/// <summary>
/// The shared team/request/scope admission for body-blind run views. It is also the single selected-cell rule: lineage
/// attempts are ordered by frozen (CreatedDate, Id), then the newest attempt carrying a cell owns that coordinate.
/// </summary>
public interface IWorkflowRunViewAdmission
{
    Task<WorkflowRunViewAdmission?> AdmitAsync(Guid runId, Guid teamId, WorkflowRunViewScope scope, CancellationToken cancellationToken);
    Task<IReadOnlyList<WorkflowRunSelectedCell>> ReadSelectedCellsAsync(WorkflowRunViewAdmission admission, WorkflowRunCellCoordinate? coordinate, int take, CancellationToken cancellationToken);
}

public sealed class WorkflowRunViewAdmissionService : IWorkflowRunViewAdmission, IScopedDependency
{
    public const int MaximumLineageAttempts = 256;
    public const int MaximumIdentityCharacters = 256;

    internal const string SelectedCellsSql = """
        WITH lineage AS (
            SELECT run_id, attempt_ordinal
            FROM unnest(@run_ids::uuid[]) WITH ORDINALITY AS requested(run_id, attempt_ordinal)
        ), attempt_cells AS (
            SELECT record.run_id,
                   lineage.attempt_ordinal,
                   record.node_id,
                   record.iteration_key,
                   (array_agg(record.id ORDER BY record.sequence DESC))[1] AS latest_record_id,
                   (array_agg(record.sequence ORDER BY record.sequence DESC))[1] AS latest_record_sequence,
                   (array_agg(record.record_type ORDER BY record.sequence DESC))[1] AS latest_record_type,
                   (array_agg(record.occurred_at ORDER BY record.sequence DESC))[1] AS latest_occurred_at,
                   (array_agg(record.id ORDER BY record.sequence ASC) FILTER (WHERE record.record_type = 'node.started'))[1] AS first_started_record_id,
                   (array_agg(record.sequence ORDER BY record.sequence ASC) FILTER (WHERE record.record_type = 'node.started'))[1] AS first_started_record_sequence,
                   min(record.occurred_at) FILTER (WHERE record.record_type = 'node.started') AS first_started_at,
                   min(record.occurred_at) AS first_occurred_at
            FROM workflow_run_record AS record
            INNER JOIN lineage ON lineage.run_id = record.run_id
            WHERE record.run_id = ANY(@run_ids)
              AND record.node_id IS NOT NULL
              AND record.record_type LIKE 'node.%'
              AND (@node_id IS NULL OR record.node_id = @node_id)
              AND (@iteration_key IS NULL OR record.iteration_key = @iteration_key)
            GROUP BY record.run_id, lineage.attempt_ordinal, record.node_id, record.iteration_key
        ), cells AS (
            SELECT DISTINCT ON (node_id, iteration_key)
                   run_id, node_id, iteration_key, latest_record_id, latest_record_sequence, latest_record_type,
                   latest_occurred_at, first_started_record_id, first_started_record_sequence, first_started_at, first_occurred_at
            FROM attempt_cells
            ORDER BY node_id, iteration_key, attempt_ordinal DESC
        )
        SELECT run_id,
               CASE WHEN char_length(node_id) BETWEEN 1 AND @max_identity_chars THEN node_id END AS node_id,
               CASE WHEN char_length(iteration_key) <= @max_identity_chars THEN iteration_key END AS iteration_key,
               latest_record_id,
               latest_record_sequence,
               latest_record_type,
               coalesce(first_started_at, first_occurred_at) AS started_at,
               CASE WHEN latest_record_type IN ('node.completed', 'node.failed', 'node.skipped') THEN latest_occurred_at END AS completed_at,
               first_started_record_id,
               first_started_record_sequence,
               NOT (char_length(node_id) BETWEEN 1 AND @max_identity_chars AND char_length(iteration_key) <= @max_identity_chars) AS identity_invalid
        FROM cells
        ORDER BY coalesce(first_started_at, first_occurred_at), node_id, iteration_key
        LIMIT @take
        """;

    private readonly CodeSpaceDbContext _db;

    public WorkflowRunViewAdmissionService(CodeSpaceDbContext db) { _db = db; }

    public async Task<WorkflowRunViewAdmission?> AdmitAsync(Guid runId, Guid teamId, WorkflowRunViewScope scope, CancellationToken cancellationToken)
    {
        if (!Enum.IsDefined(scope)) throw new ArgumentOutOfRangeException(nameof(scope), scope, "Unknown Workflow Run view scope.");
        var run = await _db.WorkflowRun.AsNoTracking()
            .Where(value => value.Id == runId && value.TeamId == teamId)
            .Select(value => new WorkflowRunViewHeader
            {
                Id = value.Id, RunNumber = value.RunNumber, WorkflowId = value.WorkflowId, WorkflowVersion = value.WorkflowVersion,
                SourceType = value.SourceType, ParentRunId = value.ParentRunId, RootRunId = value.RootRunId, Status = value.Status,
                HasError = value.Error != null, StartedAt = value.StartedAt, CompletedAt = value.CompletedAt, CreatedDate = value.CreatedDate,
            })
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (run is null) return null;

        if (scope == WorkflowRunViewScope.AttemptOnly || run.SourceType == WorkflowRunSourceTypes.ChildWorkflow)
            return new WorkflowRunViewAdmission(run, WorkflowRunViewAvailability.Available, new[] { new WorkflowRunViewLineageRow(run.Id, run.CreatedDate) });

        var root = run.RootRunId ?? run.Id;
        var rows = await _db.WorkflowRun.AsNoTracking()
            .Where(value => value.TeamId == teamId && value.SourceType != WorkflowRunSourceTypes.ChildWorkflow && (value.RootRunId ?? value.Id) == root)
            .OrderBy(value => value.CreatedDate).ThenBy(value => value.Id)
            .Select(value => new WorkflowRunViewLineageRow(value.Id, value.CreatedDate))
            .Take(MaximumLineageAttempts + 1)
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        return rows.Count > MaximumLineageAttempts
            ? new WorkflowRunViewAdmission(run, WorkflowRunViewAvailability.TooLarge, Array.Empty<WorkflowRunViewLineageRow>())
            : new WorkflowRunViewAdmission(run, WorkflowRunViewAvailability.Available, rows);
    }

    public async Task<IReadOnlyList<WorkflowRunSelectedCell>> ReadSelectedCellsAsync(WorkflowRunViewAdmission admission,
        WorkflowRunCellCoordinate? coordinate, int take, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(admission);
        if (admission.LineageAvailability != WorkflowRunViewAvailability.Available || admission.Lineage.Count == 0) return Array.Empty<WorkflowRunSelectedCell>();
        if (take <= 0) throw new ArgumentOutOfRangeException(nameof(take));

        var connection = _db.Database.GetDbConnection();
        var closeAfter = connection.State != ConnectionState.Open;
        if (closeAfter) await _db.Database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = SelectedCellsSql;
            Add(command, "run_ids", admission.Lineage.Select(value => value.Id).ToArray());
            Add(command, "node_id", DbType.String, coordinate is null ? DBNull.Value : coordinate.NodeId);
            Add(command, "iteration_key", DbType.String, coordinate is null ? DBNull.Value : coordinate.IterationKey);
            Add(command, "take", DbType.Int32, take);
            Add(command, "max_identity_chars", DbType.Int32, MaximumIdentityCharacters);

            var rows = new List<WorkflowRunSelectedCell>(Math.Min(take, 256));
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                rows.Add(new WorkflowRunSelectedCell
                {
                    SourceRunId = reader.GetGuid(0), NodeId = NullableString(reader, 1), IterationKey = NullableString(reader, 2),
                    StateRecordId = reader.GetGuid(3), StateRecordSequence = reader.GetInt64(4), RecordType = reader.GetString(5),
                    StartedAt = NullableInstant(reader, 6), CompletedAt = NullableInstant(reader, 7),
                    FirstStartedRecordId = NullableGuid(reader, 8), FirstStartedRecordSequence = NullableInt64(reader, 9),
                    IdentityInvalid = reader.GetBoolean(10),
                });
            }
            return rows;
        }
        finally
        {
            if (closeAfter) await _db.Database.CloseConnectionAsync().ConfigureAwait(false);
        }
    }

    private static string? NullableString(DbDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
    private static DateTimeOffset? NullableInstant(DbDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetFieldValue<DateTimeOffset>(ordinal);
    private static Guid? NullableGuid(DbDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetGuid(ordinal);
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
}

public sealed record WorkflowRunViewAdmission(WorkflowRunViewHeader Header, WorkflowRunViewAvailability LineageAvailability, IReadOnlyList<WorkflowRunViewLineageRow> Lineage);
public sealed record WorkflowRunViewLineageRow(Guid Id, DateTimeOffset CreatedDate);
public sealed record WorkflowRunCellCoordinate(string NodeId, string IterationKey);

public sealed record WorkflowRunSelectedCell
{
    public required Guid SourceRunId { get; init; }
    public string? NodeId { get; init; }
    public string? IterationKey { get; init; }
    public required Guid StateRecordId { get; init; }
    public required long StateRecordSequence { get; init; }
    public required string RecordType { get; init; }
    public DateTimeOffset? StartedAt { get; init; }
    public DateTimeOffset? CompletedAt { get; init; }
    public Guid? FirstStartedRecordId { get; init; }
    public long? FirstStartedRecordSequence { get; init; }
    public required bool IdentityInvalid { get; init; }

    public NodeStatus Status => RecordType switch
    {
        WorkflowRunRecordTypes.NodeStarted => NodeStatus.Running,
        WorkflowRunRecordTypes.NodeCompleted => NodeStatus.Success,
        WorkflowRunRecordTypes.NodeFailed => NodeStatus.Failure,
        WorkflowRunRecordTypes.NodeSkipped => NodeStatus.Skipped,
        WorkflowRunRecordTypes.NodeSuspended => NodeStatus.Suspended,
        _ => NodeStatus.Pending,
    };
}

public sealed record WorkflowRunViewHeader
{
    public required Guid Id { get; init; }
    public required long RunNumber { get; init; }
    public Guid? WorkflowId { get; init; }
    public int? WorkflowVersion { get; init; }
    public required string SourceType { get; init; }
    public Guid? ParentRunId { get; init; }
    public Guid? RootRunId { get; init; }
    public required WorkflowRunStatus Status { get; init; }
    public required bool HasError { get; init; }
    public DateTimeOffset? StartedAt { get; init; }
    public DateTimeOffset? CompletedAt { get; init; }
    public required DateTimeOffset CreatedDate { get; init; }
}
