using CodeSpace.Core.Persistence.Db;

namespace CodeSpace.Core.Services.Workflows.Artifacts.Retention;

/// <summary>What an artifact's reference status is, as far as the oracle can establish it.</summary>
public enum ArtifactReferenceVerdict
{
    /// <summary>At least one row somewhere still points at the artifact. Never collect.</summary>
    Referenced = 1,

    /// <summary>No reference exists at ANY enumerated site. A necessary condition for collection, never a sufficient one.</summary>
    Unreferenced = 2,

    /// <summary>The question could not be answered. Read as "keep" everywhere it is consumed.</summary>
    Indeterminate = 3,
}

/// <summary>
/// Answers "does anything still reference this artifact" over every reference site that is enumerable in a column.
/// Deliberately narrow (Rule 7): one question, no listing, no deletion, no policy.
///
/// <para>This oracle is COMPLETE over columns and silent about everything else. It does not read
/// <c>workflow_run_record.payload_json</c>, <c>workflow_run.outputs_jsonb</c>, <c>agent_run.result_jsonb</c> or the
/// bytes of other artifacts, all of which can carry an artifact reference. That is why it is only ever asked about an
/// artifact carrying a retention declaration — a declaration exists only for a class whose references are all columns,
/// and it is revoked the moment a second writer of the same bytes could have put the id somewhere else.</para>
/// </summary>
public interface IArtifactReferenceOracle
{
    /// <summary>
    /// Classify <paramref name="artifactId"/> using <paramref name="db"/>, so a caller holding a transaction — the
    /// collector, which must re-verify inside the same transaction as its DELETE — probes its own snapshot rather than
    /// a second connection's. Fail-closed: any failure to reach or interpret a reference site yields
    /// <see cref="ArtifactReferenceVerdict.Indeterminate"/>, never <see cref="ArtifactReferenceVerdict.Unreferenced"/>.
    /// </summary>
    Task<ArtifactReferenceVerdict> ClassifyAsync(CodeSpaceDbContext db, Guid artifactId, CancellationToken cancellationToken);
}
