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

/// <summary>
/// Bounded, non-authoritative artifact read for UI/inspection projections. Agent, oracle, and completion consumers must
/// continue to use the strict full-byte path until the versioned storage driver router provides verified range reads.
/// </summary>
public interface IArtifactRangeReader
{
    Task<ArtifactRangeReadResult> ReadRangeAsync(Guid teamId, Guid artifactId, long offset, int length, CancellationToken cancellationToken);
}

public enum ArtifactRangeReadState
{
    Available = 0,
    MetadataMissing = 1,
    PhysicalObjectMissing = 2,
    IntegrityFailure = 3,
    BackendUnavailable = 4,
    AccessDenied = 5,
    InvalidOffset = 6,
}

public sealed class ArtifactRangeReadResult
{
    public ArtifactRangeReadState State { get; }
    public byte[]? Bytes { get; }
    public long? TotalLength { get; }
    public string? Sha256 { get; }
    public string? ContentType { get; }
    public bool IntegrityVerified { get; }

    private ArtifactRangeReadResult(ArtifactRangeReadState state, byte[]? bytes, long? totalLength, string? sha256, string? contentType, bool integrityVerified)
    {
        State = state;
        Bytes = bytes;
        TotalLength = totalLength;
        Sha256 = sha256;
        ContentType = contentType;
        IntegrityVerified = integrityVerified;
    }

    public static ArtifactRangeReadResult Available(byte[] bytes, long totalLength, string sha256, string contentType, bool integrityVerified)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        if (totalLength < 0 || bytes.LongLength > totalLength) throw new ArgumentOutOfRangeException(nameof(totalLength));
        if (string.IsNullOrWhiteSpace(sha256) || string.IsNullOrWhiteSpace(contentType)) throw new ArgumentException("Available artifact range reads require identity metadata.");
        return new ArtifactRangeReadResult(ArtifactRangeReadState.Available, bytes, totalLength, sha256, contentType, integrityVerified);
    }

    public static ArtifactRangeReadResult Failed(ArtifactRangeReadState state, long? totalLength = null, string? sha256 = null, string? contentType = null)
    {
        if (state == ArtifactRangeReadState.Available) throw new ArgumentException("A failed artifact range read cannot use the Available state.", nameof(state));
        return new ArtifactRangeReadResult(state, null, totalLength, sha256, contentType, false);
    }
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
