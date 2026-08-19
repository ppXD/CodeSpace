namespace CodeSpace.Messages.Artifacts;

/// <summary>
/// The producer class a retention declaration is filed under. A class exists only when the COMPLETE set of places
/// that can reference the artifact is enumerable in database columns — an artifact whose id can also reach a JSON
/// payload or another artifact's bytes has no class and is therefore never a reap candidate.
/// </summary>
public enum ArtifactRetentionClass
{
    /// <summary>
    /// The captured bytes of a declared deliverable behind an <c>artifact_manifest</c> row. The only reference
    /// <c>ArtifactManifestStore</c> writes is <c>artifact_manifest.content_artifact_id</c>, and it writes it after the
    /// bytes land — so a crash in between leaves bytes that nothing ever pointed at, which is exactly what this class
    /// reclaims.
    /// </summary>
    ArtifactManifestContent = 1,
}

/// <summary>
/// A retention declaration's lifecycle. <see cref="Declared"/> and <see cref="Quarantined"/> are the live states the
/// reaper claims; every other state is terminal and means the artifact is KEPT for good. There is deliberately no
/// terminal state meaning "delete later" — collection removes the artifact and the declaration together.
/// </summary>
public enum ArtifactRetentionState
{
    /// <summary>The producer declared the write; the reaper has not yet established whether the reference landed.</summary>
    Declared = 1,

    /// <summary>The reaper observed no reference. Collection waits for the class's quarantine window to elapse from this observation.</summary>
    Quarantined = 2,

    /// <summary>A reference was found. Terminal — the artifact is never a candidate again.</summary>
    Referenced = 3,

    /// <summary>A second writer of the same bytes took the id, or the producer withdrew the declaration. Terminal, because this ledger can no longer claim to enumerate the artifact's references.</summary>
    Revoked = 4,

    /// <summary>Reference status could not be established within the retry budget. Terminal, and it means KEEP: "I could not tell" never resolves to "delete".</summary>
    Indeterminate = 5,
}

/// <summary>What one bounded reaper sweep did. Every claimed row lands in exactly one of these buckets.</summary>
public sealed record ArtifactRetentionSweepSummary
{
    public required int Claimed { get; init; }
    public required int Quarantined { get; init; }
    public required int Collected { get; init; }
    public required int Referenced { get; init; }
    public required int Indeterminate { get; init; }
    public required int Retried { get; init; }

    /// <summary>Claims whose settlement could not prove it still owned the lease. The work is simply re-done by a later sweep.</summary>
    public required int LostLease { get; init; }
}
