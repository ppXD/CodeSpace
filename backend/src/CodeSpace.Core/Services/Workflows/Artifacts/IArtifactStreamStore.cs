namespace CodeSpace.Core.Services.Workflows.Artifacts;

/// <summary>
/// Additive streaming write face for large, durable workflow artifacts. The existing <see cref="IArtifactStore"/>
/// remains byte-for-byte compatible for callers that already own a bounded payload; this face lets a producer hand
/// the store a length-known source without first materializing the whole object.
/// </summary>
public interface IArtifactStreamStore
{
    /// <summary>
    /// Store one re-readable source. Implementations may open the source more than once: once to admit its exact
    /// identity and again for placement, including a fresh open for every routed retry. Every open must therefore
    /// return a new readable stream at the beginning of the same immutable bytes. The store owns and disposes each
    /// returned stream before this method completes.
    /// </summary>
    Task<Guid> PutAsync(ArtifactStreamWriteRequest request, CancellationToken cancellationToken);
}

/// <summary>One tenant-scoped streaming artifact write.</summary>
public sealed record ArtifactStreamWriteRequest(Guid TeamId, string ContentType, IArtifactWriteSource Source);

/// <summary>
/// A stable, re-readable byte source. <see cref="LengthBytes"/> is an identity claim, not a hint: the store verifies
/// the first pass has exactly that many bytes before resolving a destination or persisting metadata.
/// </summary>
public interface IArtifactWriteSource
{
    long LengthBytes { get; }

    ValueTask<Stream> OpenReadAsync(CancellationToken cancellationToken);
}
