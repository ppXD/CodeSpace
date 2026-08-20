namespace CodeSpace.Core.Services.Workflows.Artifacts.Routing;

/// <summary>
/// The single write-time destination policy for every routed data class. Reads routing state exactly once, keeps
/// caller cancellation out of the storage vocabulary, and maps the control-plane outcome onto the one axis a
/// declaration can change — whether the class has a home outside the routing plane, declared by
/// <see cref="IRoutedDataClassLocalFallback"/>.
///
/// <para>Repair is deliberately NOT part of this seam. A consumer that can bootstrap its own default route does so
/// between two calls of its own; that keeps "what a control-plane state means" here and "what this class does about
/// it" with the class.</para>
/// </summary>
public sealed class RoutedDestinationResolver : IRoutedDestinationResolver
{
    private readonly IStorageRouteSnapshotResolver _routes;

    public RoutedDestinationResolver(IStorageRouteSnapshotResolver routes) => _routes = routes;

    public async Task<RoutedDestination> ResolveAsync(IRoutedDataClass dataClass, Guid teamId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(dataClass);

        var resolution = await _routes.ResolveAsync(new StorageRouteSnapshotRequest(teamId, dataClass.TypeKey), cancellationToken).ConfigureAwait(false);
        if (resolution is StorageRouteSnapshotResolution.Cancelled && cancellationToken.IsCancellationRequested)
            throw new OperationCanceledException(cancellationToken);

        if (resolution is StorageRouteSnapshotResolution.Ready ready)
            return new RoutedDestination.Routed(ready.Snapshot.StorageProfileId, ready.Snapshot.StorageProfileRevision);

        var disposition = Disposition(resolution);

        return LocalApplies(dataClass, disposition) ? new RoutedDestination.Local(disposition) : new RoutedDestination.Unusable(disposition);
    }

    /// <summary>The one policy axis. Feature-detected here and nowhere else, so a class's answer lives in its declaration rather than in a consumer's switch.</summary>
    private static bool LocalApplies(IRoutedDataClass dataClass, RoutedDestinationDisposition disposition) => dataClass is IRoutedDataClassLocalFallback
        && disposition is RoutedDestinationDisposition.NoRoute or RoutedDestinationDisposition.RouteNotActivated;

    /// <summary>
    /// Exhaustive by CASE over the outcomes the snapshot resolver names, so an outcome added later lands on
    /// <see cref="RoutedDestinationDisposition.ResolutionFailed"/> — a refusal for every class — rather than being
    /// silently absorbed into a lawful one.
    /// </summary>
    private static RoutedDestinationDisposition Disposition(StorageRouteSnapshotResolution resolution) => resolution switch
    {
        StorageRouteSnapshotResolution.Missing => RoutedDestinationDisposition.NoRoute,
        StorageRouteSnapshotResolution.RouteNotActivated => RoutedDestinationDisposition.RouteNotActivated,
        StorageRouteSnapshotResolution.RouteNotActive => RoutedDestinationDisposition.RouteNotActive,
        StorageRouteSnapshotResolution.ProfileNotActive => RoutedDestinationDisposition.ProfileNotActive,
        StorageRouteSnapshotResolution.RouteRevisionMissing or StorageRouteSnapshotResolution.ProfileMissing
            or StorageRouteSnapshotResolution.ProfileRevisionMissing or StorageRouteSnapshotResolution.Invalid => RoutedDestinationDisposition.Invalid,
        _ => RoutedDestinationDisposition.ResolutionFailed,
    };
}
