using CodeSpace.Core.Services.Workflows.Artifacts.Routing;

namespace CodeSpace.Core.Services.Workflows.Artifacts;

/// <summary>
/// Resolves the main artifact plane's versioned data class through the team storage routing control plane. Unlike the
/// Agent Run log class this one has NO bootstrap: a team with no route keeps the local backend verbatim, so adopting
/// routing is an explicit operator act rather than something a first write invents.
/// </summary>
public sealed class WorkflowArtifactDestinationResolver : IWorkflowArtifactDestinationResolver
{
    public const string DataClassTypeKey = "workflow-artifact/v1";
    private readonly IStorageRouteSnapshotResolver _routes;

    public WorkflowArtifactDestinationResolver(IStorageRouteSnapshotResolver routes) => _routes = routes;

    public async Task<WorkflowArtifactDestination> ResolveAsync(Guid teamId, CancellationToken cancellationToken)
    {
        var resolution = await _routes.ResolveAsync(new StorageRouteSnapshotRequest(teamId, DataClassTypeKey), cancellationToken).ConfigureAwait(false);
        if (resolution is StorageRouteSnapshotResolution.Cancelled && cancellationToken.IsCancellationRequested)
            throw new OperationCanceledException(cancellationToken);

        return resolution switch
        {
            StorageRouteSnapshotResolution.Ready ready => new WorkflowArtifactDestination.Routed(ready.Snapshot.StorageProfileId, ready.Snapshot.StorageProfileRevision),
            StorageRouteSnapshotResolution.Missing => new WorkflowArtifactDestination.Local(),
            StorageRouteSnapshotResolution.RouteNotActive => Unusable(WorkflowArtifactDestinationProblem.RouteNotActive),
            StorageRouteSnapshotResolution.ProfileNotActive => Unusable(WorkflowArtifactDestinationProblem.ProfileNotActive),
            StorageRouteSnapshotResolution.RouteRevisionMissing or StorageRouteSnapshotResolution.ProfileMissing
                or StorageRouteSnapshotResolution.ProfileRevisionMissing or StorageRouteSnapshotResolution.Invalid => Unusable(WorkflowArtifactDestinationProblem.Invalid),
            _ => Unusable(WorkflowArtifactDestinationProblem.ResolutionFailed),
        };
    }

    private static WorkflowArtifactDestination Unusable(WorkflowArtifactDestinationProblem problem) => new WorkflowArtifactDestination.Unusable(problem);
}
