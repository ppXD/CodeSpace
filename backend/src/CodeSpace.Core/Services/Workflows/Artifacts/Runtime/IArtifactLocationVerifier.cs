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

    /// <summary>The provider could not answer. Left untouched — including its stale <c>verified_at</c>, which is the honest record of when it was last actually known good.</summary>
    public required int Inconclusive { get; init; }
}
