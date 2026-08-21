using System.Buffers;
using System.Text;
using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Services.Sessions.Exceptions;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Dtos.Sessions;
using CodeSpace.Messages.Enums;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;

namespace CodeSpace.Core.Services.Sessions;

/// <summary>
/// Additive bounded membership page. A Tail statement admits the selector, freezes the highest current RunNumber, and
/// reads limit+1 rows in one PostgreSQL statement snapshot. Older requests carry that head forward. The head freezes
/// membership only; status/error/timing remain deliberately fresh observations and require a revision axis (or a
/// single repeatable-read request spanning all pages) before a caller may claim a fully consistent historical snapshot.
/// </summary>
internal sealed class SessionRunMetadataPageReader : ISessionRunMetadataPageReader, IScopedDependency
{
    internal const string PageSql = """
        /* session-run-metadata-page */
        WITH requested_anchor AS MATERIALIZED (
            SELECT r.id, r.session_id, COALESCE(r.root_run_id, r.id) AS root_run_id
            FROM workflow_run AS r
            WHERE @run_anchor_id IS NOT NULL AND r.id = @run_anchor_id AND r.team_id = @team_id AND r.session_id IS NOT NULL
        ), selected AS MATERIALIZED (
            SELECT s.id AS session_id, requested_anchor.root_run_id AS anchor_root_run_id
            FROM work_session AS s
            LEFT JOIN requested_anchor ON requested_anchor.session_id = s.id
            WHERE s.team_id = @team_id
              AND ((@session_id IS NOT NULL AND @run_anchor_id IS NULL AND s.id = @session_id)
                OR (@session_id IS NULL AND @run_anchor_id IS NOT NULL AND requested_anchor.id = @run_anchor_id))
              AND (@cursor_session_id IS NULL OR s.id = @cursor_session_id)
        ), admitted AS MATERIALIZED (
            SELECT selected.session_id, selected.anchor_root_run_id,
                COALESCE((
                    SELECT MAX(member.run_number)
                    FROM workflow_run AS member
                    WHERE member.session_id = selected.session_id AND member.team_id = @team_id
                      AND member.source_type <> @child_source), 0) AS current_head_run_number
            FROM selected
        ), bounded AS MATERIALIZED (
            SELECT admitted.session_id, admitted.anchor_root_run_id,
                CASE WHEN @membership_head_run_number > 0 THEN @membership_head_run_number ELSE admitted.current_head_run_number END AS membership_head_run_number
            FROM admitted
            WHERE @membership_head_run_number = 0 OR @membership_head_run_number <= admitted.current_head_run_number
        )
        SELECT bounded.session_id, bounded.anchor_root_run_id, bounded.membership_head_run_number,
            page.run_id, page.run_number, page.run_request_id, page.root_run_id, page.session_turn_index,
            page.run_status,
            page.projection_kind_prefix, page.projection_kind_bytes,
            page.source_type_prefix, page.source_type_bytes,
            page.rerun_from_node_id_prefix, page.rerun_from_node_id_bytes,
            page.created_date, page.started_at, page.completed_at, page.error_prefix, page.error_bytes,
            request.status AS request_status, request.received_at AS request_received_at
        FROM bounded
        LEFT JOIN LATERAL (
            SELECT r.id AS run_id, r.run_number, r.run_request_id, r.root_run_id, r.session_turn_index,
                r.status AS run_status,
                left(r.projection_kind, @classifier_prefix_characters) AS projection_kind_prefix,
                octet_length(r.projection_kind) AS projection_kind_bytes,
                left(r.source_type, @classifier_prefix_characters) AS source_type_prefix,
                octet_length(r.source_type) AS source_type_bytes,
                left(r.rerun_from_node_id, @node_id_prefix_characters) AS rerun_from_node_id_prefix,
                octet_length(r.rerun_from_node_id) AS rerun_from_node_id_bytes,
                r.created_date, r.started_at, r.completed_at,
                left(r.error, @error_prefix_characters) AS error_prefix,
                octet_length(r.error) AS error_bytes
            FROM workflow_run AS r
            WHERE r.session_id = bounded.session_id AND r.team_id = @team_id AND r.source_type <> @child_source
              AND r.run_number <= bounded.membership_head_run_number
              AND (@before_run_number = 0 OR r.run_number < @before_run_number)
            ORDER BY r.run_number DESC
            LIMIT @take
        ) AS page ON TRUE
        LEFT JOIN workflow_run_request AS request
          ON request.id = page.run_request_id AND request.team_id = @team_id
        """;

    private readonly CodeSpaceDbContext _db;

    public SessionRunMetadataPageReader(CodeSpaceDbContext db) { _db = db; }

    public async Task<SessionRunMetadataPage?> ReadAsync(SessionRunMetadataPageRequest request, CancellationToken cancellationToken)
    {
        var cursor = Validate(request);
        var rows = await _db.Database.SqlQueryRaw<DbRow>(PageSql, Parameters(request, cursor)).ToListAsync(cancellationToken).ConfigureAwait(false);
        if (rows.Count == 0) return null;

        var header = rows[0];
        var pageRows = rows.Where(row => row.RunId.HasValue).OrderByDescending(row => row.RunNumber).ToList();
        var hasOlder = pageRows.Count > request.Limit;
        if (hasOlder) pageRows.RemoveAt(pageRows.Count - 1);
        pageRows.Reverse();

        var nextOlder = hasOlder
            ? new SessionRunMetadataCursor(request.TeamId, header.SessionId, request.Selector.RunAnchorId, header.MembershipHeadRunNumber, pageRows[0].RunNumber!.Value).Encode()
            : null;

        return new SessionRunMetadataPage
        {
            Selector = request.Selector,
            SessionId = header.SessionId,
            Direction = request.Direction,
            RequestCursor = request.Cursor,
            MembershipHeadRunNumber = header.MembershipHeadRunNumber,
            AnchorRootRunId = header.AnchorRootRunId,
            Consistency = SessionRunMetadataConsistency.MembershipHeadOnly,
            Items = pageRows.Select(ToItem).ToList(),
            Omitted = new SessionRunMetadataOmission { Older = hasOlder, Newer = request.Direction == SessionRunMetadataPageDirection.Older },
            Continuation = new SessionRunMetadataContinuation { OlderCursor = nextOlder, ReturnToTail = request.Direction == SessionRunMetadataPageDirection.Older },
        };
    }

    private static SessionRunMetadataCursor? Validate(SessionRunMetadataPageRequest request)
    {
        var errors = new List<string>();
        if (request.TeamId == Guid.Empty) errors.Add("TeamId is required.");
        if (request.Selector == null) errors.Add("Selector is required.");
        else if (!ValidSelector(request.Selector)) errors.Add("Selector must contain exactly one non-empty identity matching its Kind.");
        if (!Enum.IsDefined(request.Direction)) errors.Add("Direction must be Tail or Older.");
        if (request.Limit is < 1 or > SessionRunMetadataPageRequest.MaximumLimit) errors.Add($"Limit must be between 1 and {SessionRunMetadataPageRequest.MaximumLimit}.");

        SessionRunMetadataCursor? cursor = null;
        if (request.Direction == SessionRunMetadataPageDirection.Tail)
        {
            if (request.Cursor != null) errors.Add("Tail does not accept a cursor.");
        }
        else if (request.Direction == SessionRunMetadataPageDirection.Older)
        {
            try { cursor = SessionRunMetadataCursor.Decode(request.Cursor); }
            catch (InvalidOperationException ex) { errors.Add(ex.Message); }
        }

        if (cursor is { } value && request.Selector != null && (value.TeamId != request.TeamId || !MatchesSelector(value, request.Selector)))
            errors.Add("Cursor does not belong to the requested team and selector.");
        if (errors.Count > 0) throw new SessionRunMetadataPageRequestException(errors);
        return cursor;
    }

    private static bool ValidSelector(SessionRunMetadataSelector selector) => selector.Kind switch
    {
        SessionRunMetadataSelectorKind.Session => selector.SessionId is { } sessionId && sessionId != Guid.Empty && selector.RunAnchorId == null,
        SessionRunMetadataSelectorKind.RunAnchor => selector.RunAnchorId is { } runId && runId != Guid.Empty && selector.SessionId == null,
        _ => false,
    };

    private static bool MatchesSelector(SessionRunMetadataCursor cursor, SessionRunMetadataSelector selector) => selector.Kind switch
    {
        SessionRunMetadataSelectorKind.Session => cursor.SessionId == selector.SessionId && cursor.RunAnchorId == null,
        SessionRunMetadataSelectorKind.RunAnchor => cursor.RunAnchorId == selector.RunAnchorId,
        _ => false,
    };

    private static object[] Parameters(SessionRunMetadataPageRequest request, SessionRunMetadataCursor? cursor) =>
    [
        Uuid("team_id", request.TeamId),
        Uuid("session_id", request.Selector.SessionId),
        Uuid("run_anchor_id", request.Selector.RunAnchorId),
        Uuid("cursor_session_id", cursor?.SessionId),
        new NpgsqlParameter<long>("membership_head_run_number", cursor?.MembershipHeadRunNumber ?? 0),
        new NpgsqlParameter<long>("before_run_number", cursor?.BeforeRunNumber ?? 0),
        new NpgsqlParameter<int>("take", request.Limit + 1),
        new NpgsqlParameter<string>("child_source", WorkflowRunSourceTypes.ChildWorkflow),
        new NpgsqlParameter<int>("classifier_prefix_characters", SessionRunMetadataPageRequest.MaximumClassifierBytes),
        new NpgsqlParameter<int>("node_id_prefix_characters", SessionRunMetadataPageRequest.MaximumNodeIdBytes),
        new NpgsqlParameter<int>("error_prefix_characters", SessionRunMetadataPageRequest.MaximumErrorBytes),
    ];

    private static NpgsqlParameter Uuid(string name, Guid? value) => new(name, NpgsqlDbType.Uuid) { Value = value.HasValue ? value.Value : DBNull.Value };

    private static SessionRunMetadataItem ToItem(DbRow row) => new()
    {
        RunId = row.RunId!.Value,
        RunNumber = row.RunNumber!.Value,
        RunRequestId = row.RunRequestId!.Value,
        RootRunId = row.RootRunId,
        SessionTurnIndex = row.SessionTurnIndex,
        Status = Enum.Parse<WorkflowRunStatus>(row.RunStatus!),
        ProjectionKind = Bound(row.ProjectionKindPrefix, row.ProjectionKindBytes, SessionRunMetadataPageRequest.MaximumClassifierBytes),
        SourceType = Bound(row.SourceTypePrefix, row.SourceTypeBytes, SessionRunMetadataPageRequest.MaximumClassifierBytes, required: true),
        RerunFromNodeId = Bound(row.RerunFromNodeIdPrefix, row.RerunFromNodeIdBytes, SessionRunMetadataPageRequest.MaximumNodeIdBytes),
        CreatedDate = row.CreatedDate!.Value,
        StartedAt = row.StartedAt,
        CompletedAt = row.CompletedAt,
        Error = Bound(row.ErrorPrefix, row.ErrorBytes, SessionRunMetadataPageRequest.MaximumErrorBytes),
        RequestStatus = Enum.Parse<WorkflowRunRequestStatus>(row.RequestStatus!),
        RequestReceivedAt = row.RequestReceivedAt!.Value,
    };

    private static SessionRunMetadataText Bound(string? candidate, long? sizeBytes, int maximumBytes, bool required = false)
    {
        if (sizeBytes is null)
            return candidate is null && !required
                ? new SessionRunMetadataText { State = SessionRunMetadataTextState.None, SizeBytes = 0 }
                : Corrupt(sizeBytes);
        if (sizeBytes < 0 || candidate is null) return Corrupt(sizeBytes);

        var remaining = candidate.AsSpan();
        var prefixBytes = 0;
        var prefixCharacters = 0;
        while (!remaining.IsEmpty)
        {
            var status = Rune.DecodeFromUtf16(remaining, out var rune, out var consumed);
            if (status != OperationStatus.Done) return Corrupt(sizeBytes);
            if (rune.Utf8SequenceLength > maximumBytes - prefixBytes) break;
            prefixBytes += rune.Utf8SequenceLength;
            prefixCharacters += consumed;
            remaining = remaining[consumed..];
        }

        if (sizeBytes <= maximumBytes && (remaining.Length != 0 || prefixBytes != sizeBytes)) return Corrupt(sizeBytes);
        return new SessionRunMetadataText
        {
            Text = candidate[..prefixCharacters],
            SizeBytes = sizeBytes.Value,
            State = sizeBytes <= maximumBytes ? SessionRunMetadataTextState.Complete : SessionRunMetadataTextState.Truncated,
        };
    }

    private static SessionRunMetadataText Corrupt(long? sizeBytes) => new()
    {
        SizeBytes = Math.Max(0, sizeBytes ?? 0),
        State = SessionRunMetadataTextState.Corrupt,
    };

    private sealed class DbRow
    {
        public Guid SessionId { get; set; }
        public Guid? AnchorRootRunId { get; set; }
        public long MembershipHeadRunNumber { get; set; }
        public Guid? RunId { get; set; }
        public long? RunNumber { get; set; }
        public Guid? RunRequestId { get; set; }
        public Guid? RootRunId { get; set; }
        public int? SessionTurnIndex { get; set; }
        public string? RunStatus { get; set; }
        public string? ProjectionKindPrefix { get; set; }
        public long? ProjectionKindBytes { get; set; }
        public string? SourceTypePrefix { get; set; }
        public long? SourceTypeBytes { get; set; }
        public string? RerunFromNodeIdPrefix { get; set; }
        public long? RerunFromNodeIdBytes { get; set; }
        public DateTimeOffset? CreatedDate { get; set; }
        public DateTimeOffset? StartedAt { get; set; }
        public DateTimeOffset? CompletedAt { get; set; }
        public string? ErrorPrefix { get; set; }
        public long? ErrorBytes { get; set; }
        public string? RequestStatus { get; set; }
        public DateTimeOffset? RequestReceivedAt { get; set; }
    }
}
