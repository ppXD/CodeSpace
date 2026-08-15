using CodeSpace.Core.Services.Workflows.Artifacts.Routing;

namespace CodeSpace.Core.Services.Agents.AgentRunLogging;

/// <summary>
/// Resolves the versioned Agent Run log data class through the team storage routing control plane. The returned
/// profile coordinates are frozen for the write and no profile-name or single-candidate fallback is permitted.
/// </summary>
public sealed class AgentRunLogStorageResolver : IAgentRunLogStorageResolver
{
    public const string DataClassTypeKey = "agent-run-log/v1";
    private readonly IStorageRouteSnapshotResolver _routes;
    private readonly IAgentRunLogStorageReadiness _readiness;

    public AgentRunLogStorageResolver(IStorageRouteSnapshotResolver routes, IAgentRunLogStorageReadiness readiness)
    {
        _routes = routes;
        _readiness = readiness;
    }

    public async Task<AgentRunLogStorageResolution> ResolveAsync(Guid teamId, CancellationToken cancellationToken)
    {
        var request = new StorageRouteSnapshotRequest(teamId, DataClassTypeKey);
        var resolution = await _routes.ResolveAsync(request, cancellationToken).ConfigureAwait(false);
        if (resolution is StorageRouteSnapshotResolution.Cancelled && cancellationToken.IsCancellationRequested)
            throw new OperationCanceledException(cancellationToken);
        if (resolution is StorageRouteSnapshotResolution.Missing)
        {
            await _readiness.EnsureDefaultRouteAsync(teamId, cancellationToken).ConfigureAwait(false);
            resolution = await _routes.ResolveAsync(request, cancellationToken).ConfigureAwait(false);
            if (resolution is StorageRouteSnapshotResolution.Cancelled && cancellationToken.IsCancellationRequested)
                throw new OperationCanceledException(cancellationToken);
        }

        return resolution switch
        {
            StorageRouteSnapshotResolution.Ready ready => new AgentRunLogStorageResolution.Ready(ready.Snapshot.StorageProfileId, ready.Snapshot.StorageProfileRevision),
            StorageRouteSnapshotResolution.Missing => Unavailable(AgentRunLogStorageProblemCode.Missing),
            StorageRouteSnapshotResolution.RouteNotActive or StorageRouteSnapshotResolution.ProfileNotActive => Unavailable(AgentRunLogStorageProblemCode.Inactive),
            StorageRouteSnapshotResolution.RouteRevisionMissing or StorageRouteSnapshotResolution.ProfileMissing
                or StorageRouteSnapshotResolution.ProfileRevisionMissing or StorageRouteSnapshotResolution.Invalid => Unavailable(AgentRunLogStorageProblemCode.Invalid),
            _ => Unavailable(AgentRunLogStorageProblemCode.ResolutionFailed),
        };
    }

    private static AgentRunLogStorageResolution Unavailable(AgentRunLogStorageProblemCode code) => new AgentRunLogStorageResolution.Unavailable(code);
}
