using CodeSpace.Core.DependencyInjection;

namespace CodeSpace.Core.Services.Workflows.Artifacts;

/// <summary>
/// Where <see cref="IArtifactStore"/> must place the NEXT offloaded artifact for a team, decided by the operator's
/// Settings-visible storage route for the versioned <c>workflow-artifact/v1</c> data class. Resolution is a WRITE-time
/// question only: bytes already stored keep the exact profile revision their location was stamped with, so a read never
/// consults this seam.
/// </summary>
public interface IWorkflowArtifactDestinationResolver : IScopedDependency
{
    Task<WorkflowArtifactDestination> ResolveAsync(Guid teamId, CancellationToken cancellationToken);
}

/// <summary>Closed, secret-free destination vocabulary. Expected policy/data readiness outcomes never throw.</summary>
public abstract record WorkflowArtifactDestination
{
    private WorkflowArtifactDestination() { }

    /// <summary>
    /// The team has no route for this data class, or has one it never activated. No route at all is the shipped state
    /// of every existing team; a Draft route is the state every route is born in and cannot return to, so both mean
    /// "this class was never cut over". The local blob backend keeps every byte of today's behaviour, including the
    /// <c>storage_url</c> shape.
    /// </summary>
    public sealed record Local : WorkflowArtifactDestination;

    /// <summary>Frozen profile coordinates for one write. The revision is stamped onto the durable artifact location.</summary>
    public sealed record Routed(Guid StorageProfileId, int StorageProfileRevision) : WorkflowArtifactDestination;

    /// <summary>
    /// A route EXISTS but cannot take new bytes. The write fails closed: silently placing the bytes on local disk
    /// would tell the operator their storage choice is in effect while it is not.
    /// </summary>
    public sealed record Unusable(WorkflowArtifactDestinationProblem Problem) : WorkflowArtifactDestination;
}

public enum WorkflowArtifactDestinationProblem
{
    /// <summary>The route was activated and is now Disabled or Retired — a stop that new writes must not bypass.</summary>
    RouteNotActive,

    /// <summary>The routed profile is not Active. Its own history stays readable; new bytes are refused.</summary>
    ProfileNotActive,

    /// <summary>The route or profile revision the pointer names is missing or self-contradictory.</summary>
    Invalid,

    /// <summary>Routing state could not be read at all.</summary>
    ResolutionFailed,
}
