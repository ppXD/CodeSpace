using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Persistence.Db;
using Microsoft.EntityFrameworkCore;

namespace CodeSpace.Core.Services.Webhooks;

/// <summary>
/// The <c>webhook_claim</c> table (migration 0244). One upsert decides a claim, inside the delivery's transaction, and
/// touches no row but its own key. Expired rows are taken over in place by the next claim on the same key; everything
/// else expired is cleared by <see cref="PurgeExpiredAsync"/>, from the recurring sweep, outside any delivery.
/// </summary>
public sealed class WebhookClaimStore : IWebhookClaimStore, IScopedDependency
{
    /// <summary>How many expired claims one sweep statement deletes. Each batch commits on its own, so a backlog never holds a long lock that a delivery's claim could queue behind.</summary>
    internal const int PurgeBatch = 1000;

    private readonly CodeSpaceDbContext _db;

    public WebhookClaimStore(CodeSpaceDbContext db) { _db = db; }

    /// <summary>
    /// Insert, or take over a row whose window ended or that this holder already holds. A row held by someone else inside
    /// its window matches the conflict but not the <c>WHERE</c>, so nothing is written and zero rows come back.
    /// </summary>
    public async Task<bool> TryClaimAsync(string key, string holder, TimeSpan holdFor, CancellationToken cancellationToken)
    {
        var written = await _db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO webhook_claim (claim_key, holder, claimed_at, expires_at)
            VALUES ({key}, {holder}, statement_timestamp(), statement_timestamp() + {holdFor})
            ON CONFLICT (claim_key) DO UPDATE
               SET holder = EXCLUDED.holder, claimed_at = EXCLUDED.claimed_at, expires_at = EXCLUDED.expires_at
             WHERE webhook_claim.expires_at <= EXCLUDED.claimed_at OR webhook_claim.holder = EXCLUDED.holder
            """, cancellationToken).ConfigureAwait(false);

        return written == 1;
    }

    /// <summary>Every expired claim, a batch at a time until a batch comes back short. Returns how many were deleted.</summary>
    public async Task<int> PurgeExpiredAsync(CancellationToken cancellationToken)
    {
        var total = 0;
        int deleted;

        do
        {
            deleted = await DeleteExpiredBatchAsync(cancellationToken).ConfigureAwait(false);
            total += deleted;
        }
        while (deleted == PurgeBatch);

        return total;
    }

    /// <summary>
    /// <c>SKIP LOCKED</c>: a row a delivery holds — an expired claim it is taking over, uncommitted — is stepped around,
    /// never waited on. The sweep waiting on a delivery while that delivery waited on a row the sweep had deleted is a
    /// deadlock; a sweep that never waits cannot be half of one.
    /// </summary>
    private async Task<int> DeleteExpiredBatchAsync(CancellationToken cancellationToken) =>
        await _db.Database.ExecuteSqlInterpolatedAsync($"""
            DELETE FROM webhook_claim
             WHERE claim_key IN (SELECT claim_key FROM webhook_claim WHERE expires_at < statement_timestamp() ORDER BY expires_at LIMIT {PurgeBatch} FOR UPDATE SKIP LOCKED)
            """, cancellationToken).ConfigureAwait(false);
}
