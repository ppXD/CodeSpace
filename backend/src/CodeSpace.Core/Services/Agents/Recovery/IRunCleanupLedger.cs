using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Messages.Agents.Recovery;
using Microsoft.EntityFrameworkCore;

namespace CodeSpace.Core.Services.Agents.Recovery;

/// <summary>
/// The durable record of what each agent run left behind — see <see cref="RunCleanupReceipt"/>. Two readers and one
/// writer, and the asymmetry between them is the design: ANY host may state that a resource is orphaned, but only the
/// host that OWNS a resource can ever settle it, so the write is monotonic and the sweep read is host-scoped.
/// </summary>
public interface IRunCleanupLedger
{
    /// <summary>
    /// Record one resource's current outcome, keyed by (run, kind, resource key). MONOTONIC: a row already
    /// <see cref="RunResourceOutcome.Completed"/> or <see cref="RunResourceOutcome.Compensated"/> is never regressed to
    /// <see cref="RunResourceOutcome.Orphaned"/> or <see cref="RunResourceOutcome.Unknown"/> — a settled resource is
    /// provably gone, so a later "still orphaned" read of it is stale by construction, not news.
    /// </summary>
    Task UpsertAsync(RunCleanupReceipt receipt, CancellationToken cancellationToken);

    /// <summary>Every receipt of the given runs, team-scoped — the Room's per-turn read. Empty for runs that left nothing behind (the overwhelming case).</summary>
    Task<IReadOnlyList<RunCleanupReceipt>> ForRunsAsync(Guid teamId, IReadOnlyCollection<Guid> agentRunIds, CancellationToken cancellationToken);

    /// <summary>The outstanding orphans whose <c>OwnerHost</c> is <paramref name="host"/>, oldest first, capped at <paramref name="batch"/> — what a sweep running ON that host can actually reclaim.</summary>
    Task<IReadOnlyList<RunCleanupReceipt>> OrphanedOnHostAsync(string host, int batch, CancellationToken cancellationToken);
}

public sealed class RunCleanupLedger : IRunCleanupLedger, IScopedDependency
{
    private readonly CodeSpaceDbContext _db;

    public RunCleanupLedger(CodeSpaceDbContext db) { _db = db; }

    public async Task UpsertAsync(RunCleanupReceipt receipt, CancellationToken cancellationToken)
    {
        if (receipt.Outcome == RunResourceOutcome.Orphaned && string.IsNullOrWhiteSpace(receipt.OwnerHost))
            throw new ArgumentException($"An orphaned {receipt.Kind} receipt must name the host that owns it — an orphan nobody can address records nothing.", nameof(receipt));

        // team_id is derived from the run rather than carried on the receipt: the resource belongs to whatever team
        // owns the run, and a writer that could state a different one would be stating a tenancy claim it has no
        // business making. A vanished run selects no row and writes nothing (the FK would refuse it anyway).
        await _db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO agent_run_cleanup_receipt
                (id, team_id, agent_run_id, fence_epoch, kind, outcome, owner_host, resource_key, recorded_by_host, recorded_at, error_code)
            SELECT {Guid.NewGuid()}, run.team_id, {receipt.AgentRunId}, {receipt.FenceEpoch}, {receipt.Kind.ToString()},
                   {receipt.Outcome.ToString()}, {receipt.OwnerHost}, {receipt.ResourceKey}, {receipt.RecordedByHost},
                   {receipt.RecordedAt}, {receipt.ErrorCode}
            FROM agent_run run WHERE run.id = {receipt.AgentRunId}
            ON CONFLICT (agent_run_id, kind, COALESCE(resource_key, '')) DO UPDATE
            SET fence_epoch = EXCLUDED.fence_epoch, outcome = EXCLUDED.outcome, owner_host = EXCLUDED.owner_host,
                recorded_by_host = EXCLUDED.recorded_by_host, recorded_at = EXCLUDED.recorded_at, error_code = EXCLUDED.error_code
            WHERE agent_run_cleanup_receipt.outcome NOT IN ('Completed', 'Compensated')
            """, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<RunCleanupReceipt>> ForRunsAsync(Guid teamId, IReadOnlyCollection<Guid> agentRunIds, CancellationToken cancellationToken)
    {
        if (agentRunIds.Count == 0) return Array.Empty<RunCleanupReceipt>();

        return await Project(_db.AgentRunCleanupReceipt.AsNoTracking()
                .Where(receipt => receipt.TeamId == teamId && agentRunIds.Contains(receipt.AgentRunId)))
            .ToListAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<RunCleanupReceipt>> OrphanedOnHostAsync(string host, int batch, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(host) || batch <= 0) return Array.Empty<RunCleanupReceipt>();

        var normalized = host.Trim().ToLowerInvariant();

        return await Project(_db.AgentRunCleanupReceipt.AsNoTracking()
                .Where(receipt => receipt.Outcome == RunResourceOutcome.Orphaned && receipt.OwnerHost != null && receipt.OwnerHost.ToLower() == normalized)
                .OrderBy(receipt => receipt.RecordedAt).ThenBy(receipt => receipt.Id)
                .Take(batch))
            .ToListAsync(cancellationToken).ConfigureAwait(false);
    }

    private static IQueryable<RunCleanupReceipt> Project(IQueryable<Persistence.Entities.AgentRunCleanupReceiptRecord> rows) =>
        rows.Select(receipt => new RunCleanupReceipt
        {
            AgentRunId = receipt.AgentRunId, FenceEpoch = receipt.FenceEpoch, Kind = receipt.Kind, Outcome = receipt.Outcome,
            OwnerHost = receipt.OwnerHost, ResourceKey = receipt.ResourceKey, RecordedByHost = receipt.RecordedByHost,
            RecordedAt = receipt.RecordedAt, ErrorCode = receipt.ErrorCode,
        });
}
