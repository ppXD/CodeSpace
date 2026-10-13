namespace CodeSpace.Core.Services.Workflows.Artifacts.Profiles;

/// <summary>
/// Restores <see cref="StorageProfileRules.StoredNamespaceFingerprint"/> on revisions that a build storing the
/// PHYSICAL namespace identity already wrote — the builds between namespace canonicalization and this release, and any
/// such pod still admitting during this rollout. The previous build's retention reaper compares stored fingerprints,
/// so those revisions must carry the value it would have written for the same configuration before a rollback can
/// bring it back.
///
/// <para>This build decides nothing on the column, so the rewrite changes nothing it does. It runs only where Hangfire
/// processes jobs.</para>
/// </summary>
public interface IStorageNamespaceFingerprintBackfillService
{
    /// <summary>Restore up to <paramref name="batchSize"/> revisions. Idempotent and safe on several workers at once: a restored revision is no longer a candidate, and each write is conditional on the row still holding the value the pass read. Returns how many revisions this pass restored.</summary>
    Task<int> BackfillAsync(int batchSize, CancellationToken cancellationToken);
}
