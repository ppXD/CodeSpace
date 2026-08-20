namespace CodeSpace.Core.Services.Workflows.Artifacts;

/// <summary>
/// The out-of-band byte store behind <see cref="IArtifactStore"/> for payloads too large to keep inline in
/// <c>workflow_artifact.inline_bytes</c>. The <see cref="IArtifactStore"/> owns the durable METADATA row (sha,
/// size, content type, tenant) in Postgres; THIS backend owns only the opaque bytes, addressed by their SHA-256,
/// and returns a <c>storage_url</c> the metadata row records. A read resolves that url back to bytes.
///
/// <para>Kept deliberately narrow (Rule 7): read/write only, no tenancy (the store already enforces it on the
/// metadata row), no listing, and no deletion — byte removal is the separate optional
/// <see cref="IArtifactBlobPurge"/>, so a write-once transport stays a valid backend instead of acquiring a method
/// it would have to throw from. A new transport — S3, a custom mirror — is a SIBLING impl behind this seam
/// (Rule 18), never a widening of <see cref="IArtifactStore"/>.</para>
/// </summary>
public interface IArtifactBlobBackend
{
    /// <summary>
    /// Persist <paramref name="bytes"/> addressed by <paramref name="sha256"/> (hex-lowercase) and return the
    /// <c>storage_url</c> to record. Content-addressed ⇒ idempotent: writing the same sha twice is a no-op that
    /// returns the same url. The url scheme is the backend's own (e.g. <c>file://</c>).
    /// </summary>
    Task<string> WriteAsync(string sha256, ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken);

    /// <summary>Whether the blob a <paramref name="storageUrl"/> claims is still THERE (P2 slice 3 — the dedup hit's existence check; content correctness is the read path's verification). Never throws for a missing blob — that's the false it exists to report.</summary>
    Task<bool> ExistsAsync(string storageUrl, CancellationToken cancellationToken);

    /// <summary>Resolve a <paramref name="storageUrl"/> this backend produced back to its bytes.</summary>
    Task<byte[]> ReadAsync(string storageUrl, CancellationToken cancellationToken);

    /// <summary>
    /// Read at most <paramref name="length"/> bytes without materializing the whole blob. This compatibility seam is
    /// used only by bounded UI/read projections while the versioned provider driver plane is rolled out.
    /// </summary>
    Task<ArtifactBlobRange> ReadRangeAsync(string storageUrl, long offset, int length, CancellationToken cancellationToken);
}

public sealed record ArtifactBlobRange(byte[] Bytes, long TotalLength);
