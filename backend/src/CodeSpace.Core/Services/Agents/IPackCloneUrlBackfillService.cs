namespace CodeSpace.Core.Services.Agents;

/// <summary>
/// Seals the clone URL of pack rows that still hold a credential in plaintext: every row imported before the pack kept
/// its pasted token sealed, soft-deleted ones included, and any an older pod writes during a rolling deploy. On a pod
/// running this code each row keeps syncing throughout — before its seal it clones from its URL, after it from the
/// sealed copy.
///
/// <para>A pod that predates the seal cannot read it. Until the rollout completes, such a pod fails to Sync a sealed
/// private pack (it clones <c>pack.url</c>, which no longer carries the token), and its import-url fails for a
/// repository whose legacy fork this pass turned into a holder plus a duplicate (two active rows under one URL). This
/// job runs only where Hangfire processes jobs, so in an Api/Worker split, rolling the Api pods out before the Worker
/// pods keeps the backfill from sealing anything an old Api pod can still serve.</para>
/// </summary>
public interface IPackCloneUrlBackfillService
{
    /// <summary>Seal up to <paramref name="batchSize"/> credential-carrying rows. Idempotent and safe on several workers at once: a sealed row is no longer a candidate, and each write is conditional on the row still holding the URL it read. Returns how many rows this pass sealed.</summary>
    Task<int> BackfillAsync(int batchSize, CancellationToken cancellationToken);
}
