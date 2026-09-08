using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Messages.Decisions;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;

namespace CodeSpace.Core.Services.Sessions;

/// <summary>
/// Narrow, bounded reader for a work thread's durable side-effect observations. It follows the trusted
/// WorkSession → WorkflowRun → AgentRun → ToolCallLedger lineage, re-keys every leg on TeamId, and deliberately
/// excludes decision.request control traffic. Only receipt metadata, the first wire content text leaf and Error cross
/// the DB/CLR boundary; the complete result_jsonb root and unrelated structured siblings never do.
/// </summary>
public interface ISessionEffectReceiptReader
{
    Task<SessionEffectReceiptPage> ReadAsync(SessionEffectReceiptRequest request, CancellationToken cancellationToken);
}

public sealed record SessionEffectReceiptRequest
{
    public required Guid TeamId { get; init; }
    public required Guid SessionId { get; init; }
    public string? Query { get; init; }
    public string? Cursor { get; init; }
    public int Limit { get; init; } = SessionEffectReceiptReader.DefaultPageSize;
}

public sealed record SessionEffectReceiptPage
{
    public required IReadOnlyList<SessionEffectReceipt> Items { get; init; }
    public string? NextCursor { get; init; }
}

public sealed record SessionEffectReceipt
{
    public required Guid Id { get; init; }
    public required Guid WorkflowRunId { get; init; }
    public required Guid AgentRunId { get; init; }
    public required string ToolKind { get; init; }
    public required string InputHash { get; init; }
    public required string Status { get; init; }
    public string? ResultText { get; init; }
    public int? ResultTextCharacters { get; init; }
    public string? Error { get; init; }
    public int? ErrorCharacters { get; init; }
    public required DateTimeOffset CreatedDate { get; init; }
}

public sealed class SessionEffectReceiptReader : ISessionEffectReceiptReader, IScopedDependency
{
    public const int DefaultPageSize = 25;
    public const int MaximumPageSize = 100;
    internal const int ExcerptCharacters = 1_200;

    private readonly CodeSpaceDbContext _db;

    public SessionEffectReceiptReader(CodeSpaceDbContext db) { _db = db; }

    public async Task<SessionEffectReceiptPage> ReadAsync(SessionEffectReceiptRequest request, CancellationToken cancellationToken)
    {
        if (request.TeamId == Guid.Empty || request.SessionId == Guid.Empty) return EmptyPage();
        if (request.Limit is < 1 or > MaximumPageSize) throw new ArgumentOutOfRangeException(nameof(request), $"Receipt page size must be between 1 and {MaximumPageSize}.");

        var cursor = SessionEffectReceiptCursor.Decode(request.Cursor, request.TeamId, request.SessionId, request.Query);
        var hasQuery = !string.IsNullOrWhiteSpace(request.Query);
        var rows = await _db.Database.SqlQueryRaw<DbRow>(ListSql,
        [
            new NpgsqlParameter<Guid>("team_id", request.TeamId),
            new NpgsqlParameter<Guid>("session_id", request.SessionId),
            new NpgsqlParameter<string>("decision_tool_kind", DecisionToolKinds.DecisionRequest),
            new NpgsqlParameter<bool>("query_provided", hasQuery),
            new NpgsqlParameter<string>("pattern", hasQuery ? $"%{EscapeLikePattern(request.Query!.Trim())}%" : ""),
            NullableParameter("before_created_date", NpgsqlDbType.TimestampTz, cursor?.CreatedDate),
            NullableParameter("before_id", NpgsqlDbType.Uuid, cursor?.Id),
            new NpgsqlParameter<int>("excerpt_characters", ExcerptCharacters),
            new NpgsqlParameter<int>("take", request.Limit + 1),
        ]).ToListAsync(cancellationToken).ConfigureAwait(false);

        var hasOlder = rows.Count > request.Limit;
        if (hasOlder) rows.RemoveAt(rows.Count - 1);

        return new SessionEffectReceiptPage
        {
            Items = rows.Select(ToReceipt).ToList(),
            NextCursor = hasOlder ? new SessionEffectReceiptCursor(rows[^1].CreatedDate, rows[^1].Id).Encode(request.TeamId, request.SessionId, request.Query) : null,
        };
    }

    /// <summary>
    /// Query refinement runs against the full named leaves before LIMIT. SELECT clips those leaves in PostgreSQL;
    /// result_jsonb itself is never selected, and only its canonical MCP wire text leaf (content[0].text) is read.
    /// The existing workflow_run(session) and tool_call_ledger(run) indexes bound both joins without a migration.
    /// </summary>
    internal const string ListSql = """
        /* session-effect-receipts:list */
        SELECT
            ledger.id,
            workflow.id AS workflow_run_id,
            agent.id AS agent_run_id,
            ledger.tool_kind,
            ledger.input_hash,
            ledger.status,
            left(ledger.result_jsonb #>> '{{content,0,text}}', @excerpt_characters) AS result_text,
            char_length(ledger.result_jsonb #>> '{{content,0,text}}') AS result_text_characters,
            left(ledger.error, @excerpt_characters) AS error,
            char_length(ledger.error) AS error_characters,
            ledger.created_date
        FROM workflow_run AS workflow
        JOIN agent_run AS agent
          ON agent.workflow_run_id = workflow.id AND agent.team_id = @team_id
        JOIN tool_call_ledger AS ledger
          ON ledger.agent_run_id = agent.id AND ledger.team_id = @team_id
        WHERE workflow.session_id = @session_id
          AND workflow.team_id = @team_id
          AND ledger.tool_kind <> @decision_tool_kind
          AND (@query_provided = FALSE
            OR ledger.tool_kind ILIKE @pattern ESCAPE '\'
            OR ledger.input_hash ILIKE @pattern ESCAPE '\'
            OR ledger.status ILIKE @pattern ESCAPE '\'
            OR COALESCE(ledger.result_jsonb #>> '{{content,0,text}}', '') ILIKE @pattern ESCAPE '\'
            OR COALESCE(ledger.error, '') ILIKE @pattern ESCAPE '\')
          AND (@before_created_date IS NULL OR (ledger.created_date, ledger.id) < (@before_created_date, @before_id))
        ORDER BY ledger.created_date DESC, ledger.id DESC
        LIMIT @take
        """;

    private static SessionEffectReceipt ToReceipt(DbRow row) => new()
    {
        Id = row.Id,
        WorkflowRunId = row.WorkflowRunId,
        AgentRunId = row.AgentRunId,
        ToolKind = row.ToolKind,
        InputHash = row.InputHash,
        Status = row.Status,
        ResultText = row.ResultText,
        ResultTextCharacters = row.ResultTextCharacters,
        Error = row.Error,
        ErrorCharacters = row.ErrorCharacters,
        CreatedDate = row.CreatedDate,
    };

    private static SessionEffectReceiptPage EmptyPage() => new() { Items = [] };

    private static NpgsqlParameter NullableParameter(string name, NpgsqlDbType type, object? value) => new(name, type) { Value = value ?? DBNull.Value };

    private static string EscapeLikePattern(string text) => text.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");

    private sealed class DbRow
    {
        public Guid Id { get; set; }
        public Guid WorkflowRunId { get; set; }
        public Guid AgentRunId { get; set; }
        public string ToolKind { get; set; } = "";
        public string InputHash { get; set; } = "";
        public string Status { get; set; } = "";
        public string? ResultText { get; set; }
        public int? ResultTextCharacters { get; set; }
        public string? Error { get; set; }
        public int? ErrorCharacters { get; set; }
        public DateTimeOffset CreatedDate { get; set; }
    }
}
