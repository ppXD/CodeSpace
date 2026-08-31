using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Services.Workflows.Artifacts;
using CodeSpace.Core.Services.Workflows.Artifacts.Retention;
using CodeSpace.Messages.Artifacts;
using Microsoft.Extensions.DependencyInjection;

namespace CodeSpace.Core.Services.Workflows.ModelCalls;

/// <summary>Creates one independent storage scope per operation so parallel materialization never shares a DbContext.</summary>
public sealed class WorkflowRunModelCallBodyArtifactWriter : IWorkflowRunModelCallBodyArtifactWriter, IScopedDependency
{
    public const string HolderKind = "workflow_run_model_call_body_capture";

    private readonly IServiceScopeFactory _scopeFactory;

    public WorkflowRunModelCallBodyArtifactWriter(IServiceScopeFactory scopeFactory) => _scopeFactory = scopeFactory;

    public async Task<ArtifactMetadata?> ReadMetadataAsync(Guid teamId, Guid artifactId, CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IArtifactStore>().GetMetadataAsync(teamId, artifactId, cancellationToken).ConfigureAwait(false);
    }

    public async Task<ArtifactMetadata> PutAsync(WorkflowRunModelCallBodyArtifactWrite request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.TeamId == Guid.Empty || request.CaptureId == Guid.Empty)
            throw new ArgumentException("A model-call body write requires persisted team and capture identities.", nameof(request));
        using var scope = _scopeFactory.CreateScope();
        var retention = scope.ServiceProvider.GetRequiredService<IArtifactRetentionWriter>();
        var store = scope.ServiceProvider.GetRequiredService<IArtifactStore>();
        var write = await retention.PutDeclaredAsync(new ArtifactRetentionWriteRequest(request.TeamId, request.Bytes, request.ContentType,
            ArtifactRetentionClass.ModelCallBodyCapture, HolderKind, request.CaptureId), cancellationToken).ConfigureAwait(false);
        return await store.GetMetadataAsync(request.TeamId, write.ArtifactId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("The artifact store returned an id without durable metadata.");
    }
}
