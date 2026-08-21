using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Services.Workflows.Artifacts;
using Microsoft.Extensions.DependencyInjection;

namespace CodeSpace.Core.Services.Workflows.ModelCalls;

/// <summary>Creates one independent storage scope per operation so parallel materialization never shares a DbContext.</summary>
public sealed class WorkflowRunModelCallBodyArtifactWriter : IWorkflowRunModelCallBodyArtifactWriter, IScopedDependency
{
    private readonly IServiceScopeFactory _scopeFactory;

    public WorkflowRunModelCallBodyArtifactWriter(IServiceScopeFactory scopeFactory) => _scopeFactory = scopeFactory;

    public async Task<ArtifactMetadata?> ReadMetadataAsync(Guid teamId, Guid artifactId, CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IArtifactStore>().GetMetadataAsync(teamId, artifactId, cancellationToken).ConfigureAwait(false);
    }

    public async Task<ArtifactMetadata> PutAsync(Guid teamId, ReadOnlyMemory<byte> bytes, string contentType, CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IArtifactStore>();
        var artifactId = await store.PutAsync(teamId, bytes, contentType, cancellationToken).ConfigureAwait(false);
        return await store.GetMetadataAsync(teamId, artifactId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("The artifact store returned an id without durable metadata.");
    }
}
