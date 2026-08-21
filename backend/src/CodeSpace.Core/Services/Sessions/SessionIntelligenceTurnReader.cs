using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Messages.Enums;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace CodeSpace.Core.Services.Sessions;

/// <summary>
/// Narrow production reader shared by <see cref="SessionContextBuilder"/> and <see cref="SessionSummarizer"/>. JSONB
/// leaf extraction still detoasts each root inside PostgreSQL, and this remains an all-lineage query because effective
/// rerun selection and the summarizer watermark need every attempt. The scaling win is at the DB/CLR boundary: the
/// multi-MiB roots never cross it, and goal/result candidates carry at most 601 Unicode scalar values before the
/// established 600-UTF-16-unit <see cref="SessionTurnText.Clip"/> runs in CLR. Fetching 601 scalars is deliberately
/// sufficient for exact astral-character parity because a scalar occupies one or two UTF-16 units.
/// </summary>
internal sealed class SessionIntelligenceTurnReader : ISessionIntelligenceTurnReader, IScopedDependency
{
    internal const int TextReadCharacters = SessionTurnText.MaxResultChars + 1;

    // Exactly the Unicode whitespace set Char.IsWhiteSpace/String.IsNullOrWhiteSpace recognizes for text PostgreSQL
    // can persist (valid Unicode scalars). The whole leaf is tested in-database so a >601-scalar value whose prefix is
    // whitespace but whose tail contains text keeps the old first-present result precedence without transporting it.
    private const string NonWhitespacePattern = "[^ \t\n\v\f\r\u0085\u00a0\u1680\u2000-\u200a\u2028\u2029\u202f\u205f\u3000]";

    internal const string ListSql = """
        /* session-intelligence-turns:list */
        SELECT
            r.id AS id,
            r.root_run_id AS root_run_id,
            r.session_turn_index AS session_turn_index,
            r.status AS status,
            r.created_date AS created_date,
            left(CASE WHEN jsonb_typeof(q.normalized_payload_json -> 'goal') = 'string' THEN q.normalized_payload_json ->> 'goal' END, @text_take) AS goal_prefix,
            CASE WHEN jsonb_typeof(q.normalized_payload_json -> 'goal') = 'string' THEN (q.normalized_payload_json ->> 'goal') ~ @non_whitespace_pattern ELSE FALSE END AS goal_has_text,
            left(CASE WHEN jsonb_typeof(r.outputs_jsonb -> 'summary') = 'string' THEN r.outputs_jsonb ->> 'summary' END, @text_take) AS summary_prefix,
            CASE WHEN jsonb_typeof(r.outputs_jsonb -> 'summary') = 'string' THEN (r.outputs_jsonb ->> 'summary') ~ @non_whitespace_pattern ELSE FALSE END AS summary_has_text,
            left(CASE WHEN jsonb_typeof(r.outputs_jsonb -> 'combined') = 'string' THEN r.outputs_jsonb ->> 'combined' END, @text_take) AS combined_prefix,
            CASE WHEN jsonb_typeof(r.outputs_jsonb -> 'combined') = 'string' THEN (r.outputs_jsonb ->> 'combined') ~ @non_whitespace_pattern ELSE FALSE END AS combined_has_text,
            left(CASE WHEN jsonb_typeof(r.outputs_jsonb -> 'reason') = 'string' THEN r.outputs_jsonb ->> 'reason' END, @text_take) AS reason_prefix,
            CASE WHEN jsonb_typeof(r.outputs_jsonb -> 'reason') = 'string' THEN (r.outputs_jsonb ->> 'reason') ~ @non_whitespace_pattern ELSE FALSE END AS reason_has_text,
            CASE WHEN jsonb_typeof(r.outputs_jsonb -> 'branch') = 'string' THEN r.outputs_jsonb ->> 'branch' END AS legacy_branch
        FROM workflow_run AS r
        LEFT JOIN workflow_run_request AS q
            ON q.id = r.run_request_id AND q.team_id = @team_id
        WHERE r.session_id = @session_id AND r.team_id = @team_id
        """;

    private readonly CodeSpaceDbContext _db;

    public SessionIntelligenceTurnReader(CodeSpaceDbContext db) { _db = db; }

    public async Task<IReadOnlyList<SessionIntelligenceTurn>> ListAsync(Guid sessionId, Guid teamId, CancellationToken cancellationToken)
    {
        var rows = await _db.Database.SqlQueryRaw<DbRow>(ListSql,
        [
            new NpgsqlParameter<Guid>("session_id", sessionId),
            new NpgsqlParameter<Guid>("team_id", teamId),
            new NpgsqlParameter<int>("text_take", TextReadCharacters),
            new NpgsqlParameter<string>("non_whitespace_pattern", NonWhitespacePattern),
        ]).ToListAsync(cancellationToken).ConfigureAwait(false);

        return rows.Select(ToTurn).ToList();
    }

    private static SessionIntelligenceTurn ToTurn(DbRow row) => new(
        row.Id, row.RootRunId, row.SessionTurnIndex, Enum.Parse<WorkflowRunStatus>(row.Status), row.CreatedDate,
        row.GoalHasText ? row.GoalPrefix : null,
        row.SummaryHasText ? row.SummaryPrefix : row.CombinedHasText ? row.CombinedPrefix : row.ReasonHasText ? row.ReasonPrefix : null,
        string.IsNullOrWhiteSpace(row.LegacyBranch) ? null : row.LegacyBranch);

    private sealed class DbRow
    {
        public Guid Id { get; set; }
        public Guid? RootRunId { get; set; }
        public int? SessionTurnIndex { get; set; }
        public string Status { get; set; } = "";
        public DateTimeOffset CreatedDate { get; set; }
        public string? GoalPrefix { get; set; }
        public bool GoalHasText { get; set; }
        public string? SummaryPrefix { get; set; }
        public bool SummaryHasText { get; set; }
        public string? CombinedPrefix { get; set; }
        public bool CombinedHasText { get; set; }
        public string? ReasonPrefix { get; set; }
        public bool ReasonHasText { get; set; }
        public string? LegacyBranch { get; set; }
    }
}
