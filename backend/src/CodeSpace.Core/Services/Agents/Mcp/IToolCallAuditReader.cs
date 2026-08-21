using System.Buffers.Text;
using System.Globalization;
using System.Text;
using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Services.Agents.Exceptions;
using CodeSpace.Messages.Dtos.Agents;
using CodeSpace.Messages.Queries.Agents;
using Microsoft.EntityFrameworkCore;

namespace CodeSpace.Core.Services.Agents.Mcp;

/// <summary>
/// Read-only operator projection of the governed, side-effecting tool-call ledger. This is intentionally separate
/// from <see cref="IToolCallLedgerService"/>: that service owns execution claims, approval transitions and exact
/// terminal replay, whose <c>ResultJson</c> is load-bearing. A list UI only needs bounded metadata and must never
/// materialize every result body as a side effect of polling the run.
/// </summary>
public interface IToolCallAuditReader
{
    Task<IReadOnlyList<ToolCallView>> ListForRunAsync(Guid agentRunId, Guid teamId, CancellationToken cancellationToken);
    Task<ToolCallPage?> PageForRunAsync(PageToolCallsQuery request, Guid teamId, CancellationToken cancellationToken);
}

public sealed class ToolCallAuditReader : IToolCallAuditReader, IScopedDependency
{
    private readonly CodeSpaceDbContext _db;

    public ToolCallAuditReader(CodeSpaceDbContext db) { _db = db; }

    public async Task<IReadOnlyList<ToolCallView>> ListForRunAsync(Guid agentRunId, Guid teamId, CancellationToken cancellationToken) =>
        await AuditRowsQuery(_db, agentRunId, teamId).ToListAsync(cancellationToken).ConfigureAwait(false);

    public async Task<ToolCallPage?> PageForRunAsync(PageToolCallsQuery request, Guid teamId, CancellationToken cancellationToken)
    {
        ToolCallAuditCursor? cursor;
        try { cursor = ToolCallAuditCursor.Decode(request.Cursor); }
        catch (InvalidOperationException ex) { throw new ToolCallPageRequestException([ex.Message]); }
        if (!await _db.AgentRun.AsNoTracking().AnyAsync(run => run.Id == request.AgentRunId && run.TeamId == teamId, cancellationToken).ConfigureAwait(false)) return null;

        var rows = await PageRowsQuery(_db, request.AgentRunId, teamId, cursor, request.Limit + 1).ToListAsync(cancellationToken).ConfigureAwait(false);
        var hasOlder = rows.Count > request.Limit;
        if (hasOlder) rows.RemoveAt(rows.Count - 1);
        rows.Reverse();

        return new ToolCallPage
        {
            AgentRunId = request.AgentRunId,
            Mode = request.Direction.ToString(),
            RequestCursor = request.Cursor,
            Items = rows.Select(ToView).ToList(),
            HasOlder = hasOlder,
            NextOlderCursor = hasOlder ? new ToolCallAuditCursor(rows[0].CreatedDate, rows[0].Id).Encode() : null,
        };
    }

    /// <summary>
    /// Exact tenant/run-scoped audit projection, ordered chronologically in PostgreSQL. Only fields serialized by
    /// <see cref="ToolCallView"/> are selected: notably not <c>ResultJson</c>, the decision envelope, approval bearer,
    /// idempotency key or input hash. Internal so the translated SQL—not merely the DTO shape—is test-pinned.
    /// </summary>
    internal static IQueryable<ToolCallView> AuditRowsQuery(CodeSpaceDbContext db, Guid agentRunId, Guid teamId) =>
        db.ToolCallLedger.AsNoTracking()
            .Where(row => row.AgentRunId == agentRunId && row.TeamId == teamId)
            .OrderBy(row => row.CreatedDate)
            .ThenBy(row => row.Id)
            .Select(row => new ToolCallView
            {
                ToolKind = row.ToolKind,
                Status = row.Status,
                CreatedDate = row.CreatedDate,
                LastModifiedDate = row.LastModifiedDate,
                Error = row.Error,
                ApprovedByUserId = row.ApprovedByUserId,
                ApprovedAt = row.ApprovedAt,
            });

    /// <summary>The sole row-bearing page query: exact tenant/run keyset and only cursor + existing safe view columns.</summary>
    internal static IQueryable<ToolCallAuditPageRow> PageRowsQuery(CodeSpaceDbContext db, Guid agentRunId, Guid teamId, ToolCallAuditCursor? cursor, int take)
    {
        var rows = db.ToolCallLedger.AsNoTracking().Where(row => row.AgentRunId == agentRunId && row.TeamId == teamId);
        if (cursor is { } value)
            rows = rows.Where(row => row.CreatedDate < value.CreatedDate || (row.CreatedDate == value.CreatedDate && row.Id.CompareTo(value.Id) < 0));

        return rows.OrderByDescending(row => row.CreatedDate).ThenByDescending(row => row.Id).Take(take)
            .Select(row => new ToolCallAuditPageRow
            {
                Id = row.Id,
                ToolKind = row.ToolKind,
                Status = row.Status,
                CreatedDate = row.CreatedDate,
                LastModifiedDate = row.LastModifiedDate,
                Error = row.Error,
                ApprovedByUserId = row.ApprovedByUserId,
                ApprovedAt = row.ApprovedAt,
            });
    }

    private static ToolCallView ToView(ToolCallAuditPageRow row) => new()
    {
        ToolKind = row.ToolKind,
        Status = row.Status,
        CreatedDate = row.CreatedDate,
        LastModifiedDate = row.LastModifiedDate,
        Error = row.Error,
        ApprovedByUserId = row.ApprovedByUserId,
        ApprovedAt = row.ApprovedAt,
    };
}

internal sealed record ToolCallAuditPageRow
{
    public required Guid Id { get; init; }
    public required string ToolKind { get; init; }
    public required CodeSpace.Messages.Agents.ToolCallLedgerStatus Status { get; init; }
    public required DateTimeOffset CreatedDate { get; init; }
    public required DateTimeOffset LastModifiedDate { get; init; }
    public string? Error { get; init; }
    public Guid? ApprovedByUserId { get; init; }
    public DateTimeOffset? ApprovedAt { get; init; }
}

internal readonly record struct ToolCallAuditCursor(DateTimeOffset CreatedDate, Guid Id)
{
    private const string WireVersion = "v1";

    public string Encode()
    {
        var raw = $"{WireVersion}\n{CreatedDate.UtcTicks.ToString(CultureInfo.InvariantCulture)}\n{Id:N}";
        return Base64Url.EncodeToString(Encoding.UTF8.GetBytes(raw));
    }

    public static ToolCallAuditCursor? Decode(string? cursor)
    {
        if (cursor == null) return null;
        if (string.IsNullOrWhiteSpace(cursor)) throw new InvalidOperationException("Invalid governed ToolCall page cursor.");
        if (cursor.Length > PageToolCallsQuery.MaximumCursorLength) throw new InvalidOperationException("Governed ToolCall page cursor is too long.");
        try
        {
            var parts = Encoding.UTF8.GetString(Base64Url.DecodeFromChars(cursor)).Split('\n');
            if (parts.Length == 3 && parts[0] == WireVersion
                && long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var ticks) && ticks >= 0 && ticks <= DateTimeOffset.MaxValue.Ticks
                && Guid.TryParseExact(parts[2], "N", out var id) && id != Guid.Empty)
                return new ToolCallAuditCursor(new DateTimeOffset(ticks, TimeSpan.Zero), id);
        }
        catch (FormatException) { }
        throw new InvalidOperationException("Invalid governed ToolCall page cursor.");
    }
}
