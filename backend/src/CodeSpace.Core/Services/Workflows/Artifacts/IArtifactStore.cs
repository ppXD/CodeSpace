namespace CodeSpace.Core.Services.Workflows.Artifacts;

/// <summary>
/// Content-addressable storage for workflow artifacts (HTTP response bodies, LLM completions,
/// fetched files). The store dedups by SHA-256 per team, picks the inline vs. external-URL
/// storage path based on size, and returns a stable id that records can reference in their
/// <c>payload_json</c>.
///
/// All writes go through <see cref="PutAsync"/>; it's idempotent (same bytes → same id).
/// Reads go through <see cref="GetBytesAsync"/> which transparently resolves inline vs.
/// storage_url. Tenant isolation is enforced by accepting <c>teamId</c> on every method
/// and scoping queries by it — cross-team reads return null.
/// </summary>
public interface IArtifactStore
{
    /// <summary>
    /// Store <paramref name="bytes"/> for <paramref name="teamId"/> with the given
    /// <paramref name="contentType"/>. Returns the artifact id. Idempotent: storing the
    /// same bytes twice from the same team returns the same id without inserting a
    /// duplicate row.
    ///
    /// Inline vs. URL routing decided by <see cref="ArtifactStoreConfig.InlineThresholdBytes"/>;
    /// the storage_url path is not yet wired, so callers that exceed the threshold get an
    /// <see cref="InvalidOperationException"/> — explicit so it surfaces at the producer side.
    /// </summary>
    Task<Guid> PutAsync(Guid teamId, ReadOnlyMemory<byte> bytes, string contentType, CancellationToken cancellationToken);

    /// <summary>
    /// Fetch raw bytes by artifact id, scoped to <paramref name="teamId"/>. Returns null
    /// when the id doesn't exist OR belongs to another team (conflated — see Rule docs).
    /// </summary>
    Task<ArtifactBytes?> GetBytesAsync(Guid teamId, Guid artifactId, CancellationToken cancellationToken);

    /// <summary>Get the artifact metadata (size, sha, content type) without loading bytes.</summary>
    Task<ArtifactMetadata?> GetMetadataAsync(Guid teamId, Guid artifactId, CancellationToken cancellationToken);
}

/// <summary>Bytes + metadata bundle returned by <see cref="IArtifactStore.GetBytesAsync"/>.</summary>
public sealed record ArtifactBytes
{
    public required Guid Id { get; init; }
    public required string Sha256 { get; init; }
    public required string ContentType { get; init; }
    public required byte[] Bytes { get; init; }
}

/// <summary>Metadata-only view from <see cref="IArtifactStore.GetMetadataAsync"/>.</summary>
public sealed record ArtifactMetadata
{
    public required Guid Id { get; init; }
    public required string Sha256 { get; init; }
    public required string ContentType { get; init; }
    public required long SizeBytes { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
}
