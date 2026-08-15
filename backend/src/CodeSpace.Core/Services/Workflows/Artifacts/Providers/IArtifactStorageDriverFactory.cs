namespace CodeSpace.Core.Services.Workflows.Artifacts.Providers;

/// <summary>Activation seam implemented by each versioned provider module.</summary>
public interface IArtifactStorageDriverFactory
{
    string ProviderTypeKey { get; }
    ValueTask<IArtifactStorageDriver> CreateAsync(ArtifactStorageDriverCreateRequest request, CancellationToken cancellationToken);
}
