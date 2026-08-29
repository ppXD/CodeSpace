using CodeSpace.Core.DependencyInjection;

namespace CodeSpace.Core.Services.Workflows.Artifacts.Runtime;

/// <summary>
/// Removes one routed CAS object's bytes without consulting the current route. The implementation resolves the exact
/// profile revision recorded on the object's location, durably claims that location before provider I/O, and finalizes
/// the observation afterwards. The first version deliberately refuses a replicated object before touching any location;
/// deleting every replica requires an object-level claim that the current schema does not yet represent. This is a
/// sibling lifecycle seam: reads and writes keep their existing contracts, while retention is the sole production caller
/// that may decide whether an object is safe to purge.
/// </summary>
public interface IArtifactCasPurgeCoordinator : IScopedDependency
{
    Task<ArtifactCasPurgeClaimResult> ClaimAsync(ArtifactCasPurgeRequest request, CancellationToken cancellationToken);
    Task<ArtifactCasPurgeResult> DeleteAsync(ArtifactCasPurgeClaim claim, CancellationToken cancellationToken);
    Task<bool> ReleaseAsync(ArtifactCasPurgeClaim claim, CancellationToken cancellationToken);
    Task<ArtifactCasPurgeResult> PurgeAsync(ArtifactCasPurgeRequest request, CancellationToken cancellationToken);
}

public sealed record ArtifactCasPurgeRequest
{
    public required Guid TeamId { get; init; }
    public required Guid ArtifactObjectId { get; init; }
    public required Guid ActorId { get; init; }

    /// <summary>
    /// Which placement to purge, when the object has more than one.
    ///
    /// <para>Null means "the only one", which is what every caller of a single-placed object wants and what the
    /// claim refuses to guess for an object with several. Naming it is how a caller draining one destination says
    /// WHICH destination, without ever implying anything about the object's other placements.</para>
    /// </summary>
    public Guid? ArtifactLocationId { get; init; }

    public TimeSpan? OperationTimeout { get; init; }
}

public abstract record ArtifactCasPurgeResult
{
    private ArtifactCasPurgeResult() { }

    public sealed record Purged(Guid LocationId, long LocationRevision, bool WasAlreadyPurged) : ArtifactCasPurgeResult;
    public sealed record Rejected(ArtifactCasProblem Problem, bool EffectMayHaveOccurred = false) : ArtifactCasPurgeResult;
}

public abstract record ArtifactCasPurgeClaimResult
{
    private ArtifactCasPurgeClaimResult() { }

    public sealed record Claimed(ArtifactCasPurgeClaim Claim) : ArtifactCasPurgeClaimResult;
    public sealed record Purged(Guid LocationId, long LocationRevision) : ArtifactCasPurgeClaimResult;
    public sealed record Rejected(ArtifactCasProblem Problem) : ArtifactCasPurgeClaimResult;
}

/// <summary>
/// Secret-free fence over one exact recorded location. A caller may persist this shape, but cannot manufacture
/// authority with it: delete/release re-read the team, object, location, state and revision before any effect.
/// </summary>
public sealed record ArtifactCasPurgeClaim
{
    public required Guid TeamId { get; init; }
    public required Guid ArtifactObjectId { get; init; }
    public required Guid LocationId { get; init; }
    public required long LocationRevision { get; init; }
    public required Guid StorageProfileId { get; init; }
    public required int StorageProfileRevision { get; init; }
    public required string ObjectKey { get; init; }
    public required string? ProviderETag { get; init; }
    public required string? ProviderObjectVersion { get; init; }
    public required Guid ActorId { get; init; }
    public required TimeSpan OperationTimeout { get; init; }
}
