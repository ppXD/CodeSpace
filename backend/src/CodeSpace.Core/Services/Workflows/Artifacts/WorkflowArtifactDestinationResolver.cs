using CodeSpace.Core.Services.Workflows.Artifacts.Routing;

namespace CodeSpace.Core.Services.Workflows.Artifacts;

/// <summary>
/// Resolves the main artifact plane's versioned data class through the team storage routing control plane. Unlike the
/// Agent Run log class this one has NO bootstrap: a team with no route keeps the local backend verbatim, so adopting
/// routing is an explicit operator act rather than something a first write invents.
///
/// <para>A route the operator created but never activated keeps the local backend too. Creating a route is one half of
/// the cutover, and the half that changes nothing: only activating it moves bytes. A route that WAS activated and is
/// now Disabled or Retired is the opposite — an explicit stop, refused rather than quietly sent back to local disk.
/// <c>StorageRouteRules.EnsureTransition</c> forbids every transition back to Draft, which is what makes those two
/// cases distinguishable from the route's state alone.</para>
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
            StorageRouteSnapshotResolution.Missing or StorageRouteSnapshotResolution.RouteNotActivated => new WorkflowArtifactDestination.Local(),
            StorageRouteSnapshotResolution.RouteNotActive => Unusable(WorkflowArtifactDestinationProblem.RouteNotActive),
            StorageRouteSnapshotResolution.ProfileNotActive => Unusable(WorkflowArtifactDestinationProblem.ProfileNotActive),
            StorageRouteSnapshotResolution.RouteRevisionMissing or StorageRouteSnapshotResolution.ProfileMissing
                or StorageRouteSnapshotResolution.ProfileRevisionMissing or StorageRouteSnapshotResolution.Invalid => Unusable(WorkflowArtifactDestinationProblem.Invalid),
            _ => Unusable(WorkflowArtifactDestinationProblem.ResolutionFailed),
        };
    }

    private static WorkflowArtifactDestination Unusable(WorkflowArtifactDestinationProblem problem) => new WorkflowArtifactDestination.Unusable(problem);
}
