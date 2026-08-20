using CodeSpace.Core.DependencyInjection;

namespace CodeSpace.Core.Services.Workflows.Artifacts.Routing;

/// <summary>
/// Where the NEXT write of one routed data class must go, for one team. This is the write-time question only: bytes
/// already stored keep the exact profile revision their <c>artifact_location</c> was stamped with, and a read resolves
/// through <see cref="RecordedArtifactLocations"/> instead — see that type for the other half of the seam.
///
/// <para>The data class is a PARAMETER rather than a constructor dependency on purpose. A consumer passes its own
/// <see cref="IRoutedDataClass"/> declaration, so this resolver never enumerates the declarations and never needs the
/// catalog; nothing here can turn into a singleton holding a scoped <c>DbContext</c> for the process lifetime.</para>
/// </summary>
public interface IRoutedDestinationResolver : IScopedDependency
{
    Task<RoutedDestination> ResolveAsync(IRoutedDataClass dataClass, Guid teamId, CancellationToken cancellationToken);
}

/// <summary>
/// Closed, secret-free destination vocabulary shared by every routed data class. Expected policy and data-readiness
/// outcomes never throw; only caller cancellation does.
/// </summary>
public abstract record RoutedDestination
{
    private RoutedDestination() { }

    /// <summary>Frozen profile coordinates for one write. The revision is what gets stamped onto the durable artifact location.</summary>
    public sealed record Routed(Guid StorageProfileId, int StorageProfileRevision) : RoutedDestination;

    /// <summary>
    /// The class was never cut over and declares a home outside the routing plane, so the write belongs there. Only
    /// reachable for a class implementing <see cref="IRoutedDataClassLocalFallback"/>, and only for
    /// <see cref="RoutedDestinationDisposition.NoRoute"/> or
    /// <see cref="RoutedDestinationDisposition.RouteNotActivated"/>.
    /// </summary>
    public sealed record Local(RoutedDestinationDisposition Disposition) : RoutedDestination;

    /// <summary>
    /// This write cannot proceed through the routing plane, and no home outside it applies. The consumer fails closed:
    /// quietly writing elsewhere would report the operator's storage choice as in effect while it is not.
    /// </summary>
    public sealed record Unusable(RoutedDestinationDisposition Disposition) : RoutedDestination;
}

/// <summary>
/// Why a resolution was not a set of frozen coordinates. It keeps distinct every control-plane state that either
/// shipped consumer reports differently — so a consumer whose own vocabulary is coarser collapses them itself rather
/// than having the choice made for it. The one place it is deliberately coarser than
/// <c>StorageRouteSnapshotResolution</c> is <see cref="Invalid"/>, which absorbs the whole
/// missing-or-self-contradictory-pointer family: no consumer distinguishes those, and the detail is a bug report
/// rather than a policy input.
/// </summary>
public enum RoutedDestinationDisposition
{
    /// <summary>The team has no route for this data class at all.</summary>
    NoRoute,

    /// <summary>A route exists in Draft — created, never activated. No later state can transition back to it.</summary>
    RouteNotActivated,

    /// <summary>The route was activated and is now Disabled or Retired: an operator deliberately stopped it.</summary>
    RouteNotActive,

    /// <summary>The routed profile is not Active. Its own history stays readable; new bytes are refused.</summary>
    ProfileNotActive,

    /// <summary>The route or profile revision the pointer names is missing or self-contradictory.</summary>
    Invalid,

    /// <summary>Routing state could not be read at all.</summary>
    ResolutionFailed,
}
