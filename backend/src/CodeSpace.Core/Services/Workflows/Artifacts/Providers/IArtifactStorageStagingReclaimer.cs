namespace CodeSpace.Core.Services.Workflows.Artifacts.Providers;

/// <summary>
/// A driver that publishes through a TEMPORARY object, and can therefore be asked to reclaim one it left behind.
///
/// <para>A sibling capability rather than a widening of <see cref="IArtifactStorageDriver"/> (Rule 7): a driver that
/// writes its destination key directly — or one whose staging is a filesystem temp file with its own flock-based
/// recovery — stages nothing a caller could name, and must not have to answer for one.</para>
///
/// <para>What it is for: a remote staged publish is upload-then-server-side-copy, and the staging object is deleted in
/// a <c>finally</c>. That covers a caught fault. It does NOT cover the process being KILLED between the upload and the
/// copy, and what is left then is the worst shape an object can have — bytes in the bucket, billed, that no
/// <c>artifact_location</c>, no <c>artifact_object</c> and no verifier can reach, because nothing anywhere recorded
/// that they exist.</para>
///
/// <para>So the key is minted BEFORE the write and handed to the caller, which records it durably
/// (<c>artifact_transfer_intent.temporary_object_key</c>) while its worker lease is live. The database then names the
/// orphan candidate before the first byte lands, and the abandoned-transfer sweep — provider-neutral, fenced on that
/// same lease — reclaims it through <see cref="DiscardStagingAsync"/> once the writer's lease lapses.</para>
/// </summary>
public interface IArtifactStorageStagingReclaimer
{
    /// <summary>
    /// Names the temporary object a write may be told to occupy, without occupying it. Unique per call, so two writes
    /// to one key never share a staging object, and stable enough to be persisted and handed back later.
    /// </summary>
    string MintStagingObjectKey();

    /// <summary>
    /// Deletes a temporary object this driver minted. Absent is SUCCESS — the ordinary case is a writer whose own
    /// cleanup already ran — and a key outside this driver's staging area must be refused as
    /// <see cref="ArtifactStorageErrorCode.InvalidRequest"/> rather than deleted, which is what keeps a reclaimer that
    /// is handed a wrong or stale value from ever reaching a published object.
    /// </summary>
    ValueTask<ArtifactStorageDeleteResult> DiscardStagingAsync(string stagingObjectKey, CancellationToken cancellationToken);
}
