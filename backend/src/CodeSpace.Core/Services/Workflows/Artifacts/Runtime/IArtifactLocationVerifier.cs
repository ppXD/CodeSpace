using CodeSpace.Core.DependencyInjection;

namespace CodeSpace.Core.Services.Workflows.Artifacts.Runtime;

/// <summary>
/// Re-asks the provider whether a recorded location's bytes are still there, and still what was recorded.
///
/// <para><c>artifact_location</c> declares Missing and Corrupt, and until now no production code wrote either. Bytes
/// that vanished or rotted at the provider AFTER commit stayed <c>Available</c> forever, with <c>verified_at</c>
/// frozen at the write instant and an ORDER BY as its only consumer. The condition became knowable only when a person
/// opened that artifact and the read threw.</para>
///
/// <para>Marking a location is the DANGEROUS direction: a location wrongly moved off Available stops being readable,
/// which is worse than the silence it replaces. So this only ever demotes on an answer that is definitive — the
/// provider saying the object is not there, or returning one whose size or ETag disagrees with what was recorded. Any
/// other outcome leaves the row exactly as it was, and pointedly does NOT refresh <c>verified_at</c>, so a destination
/// that keeps failing stays visibly stale rather than looking freshly checked.</para>
/// </summary>
public interface IArtifactLocationVerifier : IScopedDependency
{
    /// <summary>Verifies a bounded batch of the least recently verified locations a destination can still speak for. Returns what it observed.</summary>
    Task<ArtifactLocationVerificationSummary> VerifyStaleAsync(int batchSize, CancellationToken cancellationToken);
}

/// <summary>What one pass saw. Counts rather than rows: the operator-facing question is "is anything rotting", and a per-row answer belongs on the artifact itself.</summary>
public sealed record ArtifactLocationVerificationSummary
{
    public required int Checked { get; init; }

    /// <summary>Still present and still matching. Their <c>verified_at</c> moved forward.</summary>
    public required int Confirmed { get; init; }

    /// <summary>Was Missing, and the destination now serves the recorded object again. Returned to Available — demotion is reversible on the same evidence that accepted the placement.</summary>
    public required int Restored { get; init; }

    /// <summary>The provider says the object is not there, AND the destination itself proved it can still answer. Demoted to Missing, and no longer served to a reader.</summary>
    public required int Missing { get; init; }

    /// <summary>Present, but not the object that was recorded. Demoted to Corrupt rather than served.</summary>
    public required int Corrupt { get; init; }

    /// <summary>
    /// Nothing was established about the object. The provider could not answer — or the pass could not even find out
    /// WHICH destination to ask, because the read that resolves it was itself refused. Left untouched, including its
    /// stale <c>verified_at</c>, which is the honest record of when it was last actually known good.
    ///
    /// <para>Distinct from <see cref="Unrecorded"/> in the direction that matters to whoever reads the number: this
    /// says the answer never arrived, that one says the answer arrived and could not be written down.</para>
    /// </summary>
    public required int Inconclusive { get; init; }

    /// <summary>
    /// The provider answered, and the database refused to record it. Left untouched, exactly as an
    /// <see cref="Inconclusive"/> row is — but counted apart from one, because the two say different things about the
    /// deployment. A handful here is passes racing each other over the same rows, which is expected and harmless. A
    /// number close to <see cref="Checked"/> is a pass that observed a whole batch and wrote none of it down, and
    /// folding that into <see cref="Inconclusive"/> would leave it indistinguishable from a destination being offline.
    /// </summary>
    public required int Unrecorded { get; init; }

    /// <summary>
    /// Selected into the batch and then dropped unasked, because the destination behind the row had ALREADY failed to
    /// answer earlier in this same pass. Left untouched exactly as an <see cref="Inconclusive"/> row is, including its
    /// stale <c>verified_at</c>.
    ///
    /// <para>Its own count rather than more <see cref="Inconclusive"/>, because a destination that is down costs one
    /// round trip and N drops, not N round trips. Folded together, forty dropped rows would read as forty destinations
    /// that were asked and said nothing — a deployment-wide outage reported for a fault one bucket wide.</para>
    /// </summary>
    public required int Skipped { get; init; }
}
