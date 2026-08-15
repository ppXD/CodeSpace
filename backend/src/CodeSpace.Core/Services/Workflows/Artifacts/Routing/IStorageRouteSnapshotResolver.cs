using CodeSpace.Core.DependencyInjection;

namespace CodeSpace.Core.Services.Workflows.Artifacts.Routing;

/// <summary>
/// Resolves one team/data-class control-plane pointer into exact immutable route and profile coordinates. The
/// contract deliberately contains no provider configuration, credential reference, secret, driver or mutable
/// entity: a write may persist a <see cref="StorageRouteSnapshot"/> without consulting routing state again.
/// </summary>
public interface IStorageRouteSnapshotResolver : IScopedDependency
{
    Task<StorageRouteSnapshotResolution> ResolveAsync(StorageRouteSnapshotRequest request, CancellationToken cancellationToken);
}

public sealed record StorageRouteSnapshotRequest(Guid TeamId, string DataClassTypeKey);

/// <summary>Frozen coordinates selected by one route authorization read.</summary>
public sealed record StorageRouteSnapshot
{
    public required Guid RouteId { get; init; }
    public required int RouteRevision { get; init; }
    public required string DataClassTypeKey { get; init; }
    public required Guid StorageProfileId { get; init; }
    public required int StorageProfileRevision { get; init; }
    public required string ProviderTypeKey { get; init; }
    public required string NamespaceFingerprint { get; init; }
}

/// <summary>Closed, secret-free resolution vocabulary. Expected policy/data readiness failures never throw.</summary>
public abstract record StorageRouteSnapshotResolution
{
    private StorageRouteSnapshotResolution() { }

    public sealed record Ready(StorageRouteSnapshot Snapshot) : StorageRouteSnapshotResolution;
    public sealed record Missing : StorageRouteSnapshotResolution;
    public sealed record RouteNotActive : StorageRouteSnapshotResolution;
    public sealed record RouteRevisionMissing : StorageRouteSnapshotResolution;
    public sealed record ProfileMissing : StorageRouteSnapshotResolution;
    public sealed record ProfileNotActive : StorageRouteSnapshotResolution;
    public sealed record ProfileRevisionMissing : StorageRouteSnapshotResolution;
    public sealed record Invalid(StorageRouteSnapshotInvalidReason Reason) : StorageRouteSnapshotResolution;
    public sealed record Cancelled : StorageRouteSnapshotResolution;
}

public enum StorageRouteSnapshotInvalidReason
{
    Request,
    DataClassTypeKey,
    RouteState,
    RouteRevision,
    ProfileRevisionMode,
    ProfileState,
    ProfileRevision,
    ProviderTypeKey,
    NamespaceFingerprint,
}
