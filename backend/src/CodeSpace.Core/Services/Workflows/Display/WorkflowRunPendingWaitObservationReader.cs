using System.Data;
using System.Data.Common;
using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Dtos.Workflows;
using Microsoft.EntityFrameworkCore;

namespace CodeSpace.Core.Services.Workflows.Display;

/// <summary>
/// Reads one pending-wait action surface without materializing the run graph or wait payload in the application
/// process. PostgreSQL extracts only the bounded prompt prefix; every unrelated payload property remains behind the
/// database seam.
/// </summary>
public sealed class WorkflowRunPendingWaitObservationReader : IWorkflowRunPendingWaitObservationReader, IScopedDependency
{
    public const int MaximumPromptCharacters = 2048;

    internal const string Sql = """
        SELECT run.id, selected.id, selected.node_id, selected.wait_kind, selected.token, selected.wake_at,
               CASE
                   WHEN selected.id IS NULL THEN 'Missing'
                   WHEN selected.payload_jsonb IS NULL
                     OR (jsonb_typeof(selected.payload_jsonb) = 'object' AND NOT selected.payload_jsonb ? 'prompt') THEN 'Missing'
                   WHEN jsonb_typeof(selected.payload_jsonb) IS DISTINCT FROM 'object'
                     OR jsonb_typeof(selected.payload_jsonb -> 'prompt') IS DISTINCT FROM 'string' THEN 'Invalid'
                   WHEN char_length(selected.payload_jsonb ->> 'prompt') > @max_prompt_chars THEN 'Truncated'
                   ELSE 'Exact'
               END AS prompt_state,
               CASE WHEN jsonb_typeof(selected.payload_jsonb) = 'object'
                       AND jsonb_typeof(selected.payload_jsonb -> 'prompt') = 'string'
                    THEN left(selected.payload_jsonb ->> 'prompt', @max_prompt_chars) END AS prompt_prefix
        FROM workflow_run AS run
        LEFT JOIN LATERAL (
            SELECT wait.id, wait.node_id, wait.wait_kind, wait.token, wait.wake_at, wait.payload_jsonb
            FROM workflow_run_wait AS wait
            WHERE wait.run_id = run.id AND wait.status = @pending_status
            ORDER BY wait.created_at DESC, wait.id DESC
            LIMIT 1
        ) AS selected ON TRUE
        WHERE run.id = @run_id AND run.team_id = @team_id
        LIMIT 1
        """;

    private readonly CodeSpaceDbContext _db;

    public WorkflowRunPendingWaitObservationReader(CodeSpaceDbContext db) { _db = db; }

    public async Task<WorkflowRunPendingWaitObservation?> ReadAsync(Guid runId, Guid teamId, CancellationToken cancellationToken)
    {
        var connection = _db.Database.GetDbConnection();
        var closeAfter = connection.State != ConnectionState.Open;
        if (closeAfter) await _db.Database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = Sql;
            Add(command, "run_id", DbType.Guid, runId);
            Add(command, "team_id", DbType.Guid, teamId);
            Add(command, "pending_status", DbType.String, WorkflowWaitStatuses.Pending);
            Add(command, "max_prompt_chars", DbType.Int32, MaximumPromptCharacters);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) return null;
            return new WorkflowRunPendingWaitObservation { RunId = reader.GetGuid(0), Wait = reader.IsDBNull(1) ? null : ReadWait(reader) };
        }
        finally
        {
            if (closeAfter) await _db.Database.CloseConnectionAsync().ConfigureAwait(false);
        }
    }

    private static WorkflowRunPendingWait ReadWait(DbDataReader reader) => new()
    {
        Id = reader.GetGuid(1), NodeId = reader.GetString(2), Kind = reader.GetString(3), Token = reader.GetString(4),
        WakeAt = reader.IsDBNull(5) ? null : reader.GetFieldValue<DateTimeOffset>(5),
        PromptState = Enum.TryParse<WorkflowRunPendingWaitPromptState>(reader.GetString(6), out var state) ? state : WorkflowRunPendingWaitPromptState.Invalid,
        PromptPrefix = reader.IsDBNull(7) ? null : reader.GetString(7),
    };

    private static void Add(DbCommand command, string name, DbType type, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.DbType = type;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }
}
