using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Persistence.Db;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;

namespace CodeSpace.Core.Services.Sessions;

/// <summary>
/// Bounded session-wide view of normalized agent events. Every lineage join is re-keyed on the trusted team and only
/// the clipped text plus structured-data presence/reference crosses the database boundary; raw data_json never does.
/// </summary>
public interface ISessionAgentEventReader
{
    Task<SessionAgentEventPage> ReadAsync(SessionAgentEventRequest request, CancellationToken cancellationToken);
}

public sealed record SessionAgentEventRequest
{
    public required Guid TeamId { get; init; }
    public required Guid SessionId { get; init; }
    public string? Query { get; init; }
    public string? Cursor { get; init; }
    public int Limit { get; init; } = SessionAgentEventReader.DefaultPageSize;
}

public sealed record SessionAgentEventPage
{
    public required IReadOnlyList<SessionAgentEvent> Items { get; init; }
    public string? NextCursor { get; init; }
}

public sealed record SessionAgentEvent
{
    public required Guid AgentRunId { get; init; }
    public required long Sequence { get; init; }
    public required string Kind { get; init; }
    public required string Text { get; init; }
    public required int TextCharacters { get; init; }
    public Guid? DataArtifactId { get; init; }
    public required bool HasInlineData { get; init; }
    public required DateTimeOffset OccurredAt { get; init; }
}

public sealed class SessionAgentEventReader : ISessionAgentEventReader, IScopedDependency
{
    public const int DefaultPageSize = 25;
    public const int MaximumPageSize = 100;
    internal const int ExcerptCharacters = 1_200;

    private readonly CodeSpaceDbContext _db;

    public SessionAgentEventReader(CodeSpaceDbContext db) { _db = db; }

    public async Task<SessionAgentEventPage> ReadAsync(SessionAgentEventRequest request, CancellationToken cancellationToken)
    {
        if (request.TeamId == Guid.Empty || request.SessionId == Guid.Empty) return EmptyPage();
        if (request.Limit is < 1 or > MaximumPageSize) throw new ArgumentOutOfRangeException(nameof(request), $"Event page size must be between 1 and {MaximumPageSize}.");

        var cursor = SessionAgentEventCursor.Decode(request.Cursor, request.TeamId, request.SessionId, request.Query);
        var hasQuery = !string.IsNullOrWhiteSpace(request.Query);
        var rows = await _db.Database.SqlQueryRaw<DbRow>(ListSql,
        [
            new NpgsqlParameter<Guid>("team_id", request.TeamId),
            new NpgsqlParameter<Guid>("session_id", request.SessionId),
            new NpgsqlParameter<bool>("query_provided", hasQuery),
            new NpgsqlParameter<string>("pattern", hasQuery ? $"%{EscapeLikePattern(request.Query!.Trim())}%" : ""),
            NullableParameter("before_sequence", NpgsqlDbType.Bigint, cursor?.Sequence),
            new NpgsqlParameter<int>("excerpt_characters", ExcerptCharacters),
            new NpgsqlParameter<int>("take", request.Limit + 1),
        ]).ToListAsync(cancellationToken).ConfigureAwait(false);

        var hasOlder = rows.Count > request.Limit;
        if (hasOlder) rows.RemoveAt(rows.Count - 1);

        return new SessionAgentEventPage
        {
            Items = rows.Select(ToEvent).ToList(),
            NextCursor = hasOlder ? new SessionAgentEventCursor(rows[^1].Sequence).Encode(request.TeamId, request.SessionId, request.Query) : null,
        };
    }

    /// <summary>
    /// Query refinement runs against full named fields before LIMIT. SELECT clips text inside PostgreSQL and projects
    /// only whether data_json exists, so an arbitrarily large structured payload never enters application memory.
    /// </summary>
    internal const string ListSql = """
        /* session-agent-events:list */
        SELECT
            event.agent_run_id,
            event.sequence,
            event.kind,
            left(event.text, @excerpt_characters) AS text,
            char_length(event.text) AS text_characters,
            event.data_artifact_id,
            event.data_json IS NOT NULL AS has_inline_data,
            event.occurred_at
        FROM workflow_run AS workflow
        JOIN agent_run AS agent
          ON agent.workflow_run_id = workflow.id AND agent.team_id = @team_id
        JOIN agent_run_event AS event
          ON event.agent_run_id = agent.id
        WHERE workflow.session_id = @session_id
          AND workflow.team_id = @team_id
          AND (@query_provided = FALSE
            OR event.kind ILIKE @pattern ESCAPE '\'
            OR event.text ILIKE @pattern ESCAPE '\'
            OR event.agent_run_id::text ILIKE @pattern ESCAPE '\')
          AND (@before_sequence IS NULL OR event.sequence < @before_sequence)
        ORDER BY event.sequence DESC
        LIMIT @take
        """;

    private static SessionAgentEvent ToEvent(DbRow row) => new()
    {
        AgentRunId = row.AgentRunId,
        Sequence = row.Sequence,
        Kind = row.Kind,
        Text = row.Text,
        TextCharacters = row.TextCharacters,
        DataArtifactId = row.DataArtifactId,
        HasInlineData = row.HasInlineData,
        OccurredAt = row.OccurredAt,
    };

    private static SessionAgentEventPage EmptyPage() => new() { Items = [] };

    private static NpgsqlParameter NullableParameter(string name, NpgsqlDbType type, object? value) => new(name, type) { Value = value ?? DBNull.Value };

    private static string EscapeLikePattern(string text) => text.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");

    private sealed class DbRow
    {
        public Guid AgentRunId { get; set; }
        public long Sequence { get; set; }
        public string Kind { get; set; } = "";
        public string Text { get; set; } = "";
        public int TextCharacters { get; set; }
        public Guid? DataArtifactId { get; set; }
        public bool HasInlineData { get; set; }
        public DateTimeOffset OccurredAt { get; set; }
    }
}
