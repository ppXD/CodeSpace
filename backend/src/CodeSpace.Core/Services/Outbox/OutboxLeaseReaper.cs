using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Persistence.Db;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace CodeSpace.Core.Services.Outbox;

/// <summary>
/// One SQL statement resets every expired claim — atomic, idempotent, safe to call
/// concurrently from multiple workers.
/// </summary>
public sealed class OutboxLeaseReaper : IOutboxLeaseReaper, IScopedDependency
{
    private readonly CodeSpaceDbContext _db;
    private readonly ILogger<OutboxLeaseReaper> _logger;

    public OutboxLeaseReaper(CodeSpaceDbContext db, ILogger<OutboxLeaseReaper> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task<int> ReapAsync(CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;

        var conn = (NpgsqlConnection)_db.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open)
            await conn.OpenAsync(cancellationToken).ConfigureAwait(false);

        await using var cmd = conn.CreateCommand();
        // Single UPDATE flips every stale claim back to Pending. The lease/claim columns
        // are cleared so a subsequent re-claim sees a clean slate. Attempts is NOT bumped
        // here — the reap isn't a failure of the handler, it's a failure of the worker
        // holding the claim, so we don't want it to count toward dead-letter.
        //
        // The lease_until filter MUST be < now (not <=) so a row that JUST expired but
        // whose worker is still finalising doesn't get yanked out from under them. The
        // dispatcher's own FinaliseClaimAsync writes inside a transaction so a race against
        // the reaper either lets the dispatcher's UPDATE succeed (and the reaper's affects-0-rows)
        // or the reaper succeeds (and the dispatcher's UPDATE finds a Pending row instead
        // of Claimed — still safe, the dispatcher's logic flips it back to Completed or
        // Pending+retry exactly the same way).
        cmd.CommandText = """
            UPDATE outbox_message
            SET status      = 'Pending',
                claimed_by  = NULL,
                claimed_at  = NULL,
                lease_until = NULL
            WHERE status = 'Claimed' AND lease_until < @now
            """;
        cmd.Parameters.Add(new NpgsqlParameter("now", now));

        var rowsAffected = await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        if (rowsAffected > 0)
            _logger.LogWarning("Reaped {Count} stale outbox claims (workers crashed mid-process). Rows are back to Pending and will be re-claimed.", rowsAffected);

        return rowsAffected;
    }
}
