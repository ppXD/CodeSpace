using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Decisions;
using CodeSpace.Messages.Dtos.Sessions;
using CodeSpace.Messages.Enums;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace CodeSpace.Core.Services.Sessions;

/// <summary>
/// Narrow Room/Journal session read. One command returns the exact team-scoped session header plus every top-level
/// run/attempt's metadata. JSON roots never cross the DB/CLR boundary: PostgreSQL extracts only goal/result leaves.
/// This does NOT claim to avoid JSONB detoast inside PostgreSQL; leaf extraction still detoasts the stored value.
/// Result candidates are bounded to 601 PostgreSQL characters before transport and then use the established
/// <see cref="SessionTurnText.Clip"/> contract. Goal stays intentionally unbounded because Room/Journal render the
/// user's complete authored message; only its exact lowercase <c>goal</c> leaf, never unrelated request baggage, can
/// cross the boundary. Key casing, JSON type checks, and summary→combined→reason precedence stay byte-compatible with
/// <see cref="SessionTurnText"/>; this performance path does not add a new payload dialect. Row cardinality is still
/// the full session lineage because both surfaces render the complete attempt ladder; a future paged-session contract,
/// not this read optimization, must close growth in the number of run metadata rows.
/// </summary>
internal sealed class SessionSkeletonReader : ISessionSkeletonReader, IScopedDependency
{
    internal const int ResultReadCharacters = SessionTurnText.MaxResultChars + 1;

    private const string Columns = """
        s.id AS session_id,
        s.title AS session_title,
        s.kind AS session_kind,
        s.status AS session_status,
        r.id AS run_id,
        r.root_run_id AS root_run_id,
        r.session_turn_index AS session_turn_index,
        r.status AS run_status,
        r.projection_kind AS projection_kind,
        r.source_type AS source_type,
        r.rerun_from_node_id AS rerun_from_node_id,
        r.created_date AS created_date,
        r.started_at AS started_at,
        r.completed_at AS completed_at,
        r.error AS error,
        r.completion_enforcement_mode AS completion_enforcement_mode,
        CASE WHEN jsonb_typeof(q.normalized_payload_json -> 'goal') = 'string' THEN q.normalized_payload_json ->> 'goal' END AS goal_camel,
        left(CASE WHEN jsonb_typeof(r.outputs_jsonb -> 'summary') = 'string' THEN r.outputs_jsonb ->> 'summary' END, @result_take) AS summary_camel,
        left(CASE WHEN jsonb_typeof(r.outputs_jsonb -> 'combined') = 'string' THEN r.outputs_jsonb ->> 'combined' END, @result_take) AS combined_camel,
        left(CASE WHEN jsonb_typeof(r.outputs_jsonb -> 'reason') = 'string' THEN r.outputs_jsonb ->> 'reason' END, @result_take) AS reason_camel,
        CASE WHEN r.id IS NULL THEN FALSE ELSE
            EXISTS (
                SELECT 1
                FROM workflow_run_wait AS w
                WHERE w.run_id = r.id AND w.wait_kind = @decision_wait AND w.status = @pending_wait)
            OR EXISTS (
                SELECT 1
                FROM agent_run AS a
                WHERE a.workflow_run_id = r.id AND EXISTS (
                    SELECT 1
                    FROM tool_call_ledger AS t
                    WHERE t.agent_run_id = a.id AND t.tool_kind = @decision_tool
                        AND t.status = @awaiting_approval AND t.approved_at IS NULL))
        END AS has_pending_decision
        """;

    internal static readonly string BySessionSql = $$"""
        /* session-skeleton:by-session */
        SELECT
            {{Columns}},
            NULL::uuid AS anchor_root_id
        FROM work_session AS s
        LEFT JOIN workflow_run AS r
            ON r.session_id = s.id AND r.team_id = @team_id AND r.source_type <> @child_source
        LEFT JOIN workflow_run_request AS q
            ON q.id = r.run_request_id AND q.team_id = @team_id
        WHERE s.id = @session_id AND s.team_id = @team_id
        """;

    internal static readonly string ByRunSql = $$"""
        /* session-skeleton:by-run */
        WITH requested AS MATERIALIZED (
            SELECT r.session_id, COALESCE(r.root_run_id, r.id) AS anchor_root_id
            FROM workflow_run AS r
            WHERE r.id = @run_id AND r.team_id = @team_id AND r.session_id IS NOT NULL)
        SELECT
            {{Columns}},
            requested.anchor_root_id AS anchor_root_id
        FROM requested
        JOIN work_session AS s ON s.id = requested.session_id AND s.team_id = @team_id
        LEFT JOIN workflow_run AS r
            ON r.session_id = s.id AND r.team_id = @team_id AND r.source_type <> @child_source
        LEFT JOIN workflow_run_request AS q
            ON q.id = r.run_request_id AND q.team_id = @team_id
        """;

    private readonly CodeSpaceDbContext _db;

    public SessionSkeletonReader(CodeSpaceDbContext db) { _db = db; }

    public Task<SessionSkeleton?> GetBySessionAsync(Guid sessionId, Guid teamId, CancellationToken cancellationToken) =>
        ReadAsync(BySessionSql, Parameters(teamId, new NpgsqlParameter<Guid>("session_id", sessionId)), cancellationToken);

    public Task<SessionSkeleton?> GetByRunAsync(Guid runId, Guid teamId, CancellationToken cancellationToken) =>
        ReadAsync(ByRunSql, Parameters(teamId, new NpgsqlParameter<Guid>("run_id", runId)), cancellationToken);

    private async Task<SessionSkeleton?> ReadAsync(string sql, object[] parameters, CancellationToken cancellationToken)
    {
        var rows = await _db.Database.SqlQueryRaw<DbRow>(sql, parameters).ToListAsync(cancellationToken).ConfigureAwait(false);
        if (rows.Count == 0) return null;

        var header = rows[0];
        var runRows = rows.Where(row => row.RunId.HasValue).Select(ToRunRow).ToList();
        var turns = SessionSkeletonProjection.BuildTurns(runRows);
        var anchor = header.AnchorRootId is { } root ? turns.FirstOrDefault(turn => turn.TurnRunId == root)?.TurnIndex : null;

        return new SessionSkeleton
        {
            Id = header.SessionId,
            Title = header.SessionTitle,
            Kind = Enum.Parse<WorkSessionKind>(header.SessionKind),
            Status = Enum.Parse<WorkSessionStatus>(header.SessionStatus),
            AnchorTurnIndex = anchor,
            Turns = turns,
        };
    }

    private static SessionSkeletonProjection.RunRow ToRunRow(DbRow row) => new(
        row.RunId!.Value, row.RootRunId, row.SessionTurnIndex, Enum.Parse<WorkflowRunStatus>(row.RunStatus!), row.ProjectionKind,
        row.SourceType!, row.RerunFromNodeId, row.CreatedDate!.Value, row.StartedAt, row.CompletedAt, row.Error,
        row.CompletionEnforcementMode,
        NonBlank(row.GoalCamel),
        NonBlank(row.SummaryCamel) ?? NonBlank(row.CombinedCamel) ?? NonBlank(row.ReasonCamel),
        row.HasPendingDecision);

    private static string? NonBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    private static object[] Parameters(Guid teamId, NpgsqlParameter identity) =>
    [
        new NpgsqlParameter<Guid>("team_id", teamId), identity,
        new NpgsqlParameter<int>("result_take", ResultReadCharacters),
        new NpgsqlParameter<string>("child_source", WorkflowRunSourceTypes.ChildWorkflow),
        new NpgsqlParameter<string>("decision_wait", WorkflowWaitKinds.Decision),
        new NpgsqlParameter<string>("pending_wait", WorkflowWaitStatuses.Pending),
        new NpgsqlParameter<string>("decision_tool", DecisionToolKinds.DecisionRequest),
        new NpgsqlParameter<string>("awaiting_approval", ToolCallLedgerStatus.AwaitingApproval.ToString()),
    ];

    private sealed class DbRow
    {
        public Guid SessionId { get; set; }
        public string SessionTitle { get; set; } = "";
        public string SessionKind { get; set; } = "";
        public string SessionStatus { get; set; } = "";
        public Guid? RunId { get; set; }
        public Guid? RootRunId { get; set; }
        public int? SessionTurnIndex { get; set; }
        public string? RunStatus { get; set; }
        public string? ProjectionKind { get; set; }
        public string? SourceType { get; set; }
        public string? RerunFromNodeId { get; set; }
        public DateTimeOffset? CreatedDate { get; set; }
        public DateTimeOffset? StartedAt { get; set; }
        public DateTimeOffset? CompletedAt { get; set; }
        public string? Error { get; set; }
        public string? CompletionEnforcementMode { get; set; }
        public string? GoalCamel { get; set; }
        public string? SummaryCamel { get; set; }
        public string? CombinedCamel { get; set; }
        public string? ReasonCamel { get; set; }
        public bool HasPendingDecision { get; set; }
        public Guid? AnchorRootId { get; set; }
    }
}
