using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Persistence.Entities;

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
    Task<ArtifactCasReleaseOutcome> ReleaseAsync(ArtifactCasPurgeClaim claim, ArtifactCasReleaseEvidence evidence, CancellationToken cancellationToken);
    Task<ArtifactCasPurgeResult> PurgeAsync(ArtifactCasPurgeRequest request, CancellationToken cancellationToken);

    /// <summary>
    /// Settles a claimed placement as <c>Purged</c> WITHOUT asking the destination to delete anything, after proving
    /// the destination cannot serve the object.
    ///
    /// <para>The exit for a destination that is already gone — a deleted bucket, a revoked key, a vanished mount.
    /// Nothing else can close those records: the verifier leaves them untouched because an unanswerable destination
    /// is not evidence about an object, and a delete cannot be attempted against a destination that will not answer.
    /// Proof is a live HEAD taken inside this call, never a health row read from somewhere else.</para>
    /// </summary>
    Task<ArtifactCasAbandonResult> AbandonAsync(ArtifactCasPurgeClaim claim, CancellationToken cancellationToken);
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

/// <summary>
/// What a caller established about the destination while it held the claim it is now handing back.
///
/// <para>Releasing is not itself an observation, so the two answers cannot restore the same thing. Only a caller
/// that has just watched the destination serve the object may put the row back into a state it LEFT; a caller that
/// touched nothing may only put it back where the claim found it.</para>
/// </summary>
public enum ArtifactCasReleaseEvidence
{
    /// <summary>No driver call was made under this claim. Nothing about the placement is known that was not known before it.</summary>
    Untouched,

    /// <summary>A HEAD taken under this claim served the object.</summary>
    Served,
}

/// <summary>
/// What became of a claim that was handed back.
///
/// <para>Two unrelated failures hide inside a bare "not released", and a caller that cannot tell them apart has to
/// treat both as worth waiting on — which is an unbounded wait for the one that can never succeed. <see cref="Raced"/>
/// is about the MOMENT: the row moved under this caller, and the next attempt looks at a row this one never held.
/// <see cref="NoEvidence"/> is about the CALL: this claim carries nothing that could name a resting state, so
/// repeating it word for word has the same answer forever. Only the second one must be budgeted.</para>
/// </summary>
public enum ArtifactCasReleaseOutcome
{
    /// <summary>The placement is back in a state it can leave, and the claim is gone.</summary>
    Released,

    /// <summary>The claim was not the current one when the release ran, so it changed nothing.</summary>
    Raced,

    /// <summary>The claim is current, and nothing this call can reach establishes where the placement rests — an <see cref="ArtifactCasReleaseEvidence.Untouched"/> release of a claim taken from a <c>Deleting</c> orphan.</summary>
    NoEvidence,
}

public abstract record ArtifactCasPurgeResult
{
    private ArtifactCasPurgeResult() { }

    public sealed record Purged(Guid LocationId, long LocationRevision, bool WasAlreadyPurged) : ArtifactCasPurgeResult;
    public sealed record Rejected(ArtifactCasProblem Problem, bool EffectMayHaveOccurred = false) : ArtifactCasPurgeResult;
}

/// <summary>What an abandonment attempt established about the destination.</summary>
public abstract record ArtifactCasAbandonResult
{
    private ArtifactCasAbandonResult() { }

    /// <summary>The destination could not serve the object and the record is now Purged.</summary>
    public sealed record Abandoned(Guid LocationId, long LocationRevision, string Evidence) : ArtifactCasAbandonResult;

    /// <summary>The destination served the object. The claim was released and nothing was abandoned — this is a refusal, not a failure.</summary>
    public sealed record StillServed(Guid LocationId, string Evidence) : ArtifactCasAbandonResult;

    public sealed record Rejected(ArtifactCasProblem Problem) : ArtifactCasAbandonResult;
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

    /// <summary>
    /// The state the placement was in when this claim took it.
    ///
    /// <para>Claiming is about taking the row; deleting is about touching bytes, and the two need different answers
    /// for the same row. A claim taken from <c>Corrupt</c> may not delete — that state asserts the destination holds
    /// something which is NOT this object — and a release that established nothing puts the row back here rather
    /// than declaring it good. It is not always an answer: see <see cref="ArtifactCasReleaseEvidence"/>.</para>
    /// </summary>
    public ArtifactLocationState ClaimedFrom { get; init; } = ArtifactLocationState.Available;
}
