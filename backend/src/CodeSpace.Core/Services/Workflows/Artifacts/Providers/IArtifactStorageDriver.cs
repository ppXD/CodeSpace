namespace CodeSpace.Core.Services.Workflows.Artifacts.Providers;

/// <summary>
/// Provider-neutral, streaming artifact byte contract. It is intentionally separate from the current
/// <see cref="CodeSpace.Core.Services.Workflows.Artifacts.IArtifactBlobBackend"/> path until profile admission and migration are explicitly cut over.
/// Implementations must cooperatively observe cancellation. In particular, a cancelled write must stop accessing the
/// caller-owned content stream before its returned task settles; qualification tests must reject providers that do not.
/// </summary>
public interface IArtifactStorageDriver : IAsyncDisposable
{
    StorageProviderCapabilities Capabilities { get; }
    ValueTask<ArtifactStoragePutResult> PutAsync(ArtifactStoragePutRequest request, CancellationToken cancellationToken);
    ValueTask<ArtifactStorageHeadResult> HeadAsync(ArtifactStorageHeadRequest request, CancellationToken cancellationToken);
    ValueTask<ArtifactStorageReadResult> OpenReadAsync(ArtifactStorageReadRequest request, CancellationToken cancellationToken);
    ValueTask<ArtifactStorageDeleteResult> DeleteAsync(ArtifactStorageDeleteRequest request, CancellationToken cancellationToken);
    ValueTask<ArtifactStorageProbeResult> ProbeAsync(ArtifactStorageProbeRequest request, CancellationToken cancellationToken);
}
