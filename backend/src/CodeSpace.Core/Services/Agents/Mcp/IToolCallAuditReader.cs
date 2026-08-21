using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Messages.Dtos.Agents;
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
}

public sealed class ToolCallAuditReader : IToolCallAuditReader, IScopedDependency
{
    private readonly CodeSpaceDbContext _db;

    public ToolCallAuditReader(CodeSpaceDbContext db) { _db = db; }

    public async Task<IReadOnlyList<ToolCallView>> ListForRunAsync(Guid agentRunId, Guid teamId, CancellationToken cancellationToken) =>
        await AuditRowsQuery(_db, agentRunId, teamId).ToListAsync(cancellationToken).ConfigureAwait(false);

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
}
