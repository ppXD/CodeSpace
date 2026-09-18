using CodeSpace.Messages.Retention;

namespace CodeSpace.Core.Persistence.Entities;

/// <summary>
/// One durable record a sealed <see cref="PairedQualificationResult"/> cites, written in the seal's own transaction.
/// The retention planes read these rows as the proof that a record is still referenced — a result is a statement ABOUT
/// a set of runs, and nothing else in the schema expresses that.
///
/// <para><see cref="PinnedArtifactId"/> is a separate column rather than a typed use of <see cref="PinnedId"/> because
/// <c>ArtifactReferenceOracle</c> enumerates reference sites by COLUMN NAME: an artifact pin has to sit in a column
/// ending in <c>artifact_id</c>, and nothing else may. Exactly one of the two is set, and the database's CHECK ties
/// which one to <see cref="Kind"/>.</para>
/// </summary>
public sealed class PairedQualificationResultPin : IEntity<Guid>
{
    public Guid Id { get; set; }
    public Guid ResultId { get; set; }
    public DurablePinKind Kind { get; set; }
    public Guid? PinnedId { get; set; }
    public Guid? PinnedArtifactId { get; set; }
    public DateTimeOffset PinnedAt { get; set; }

    /// <summary>The pinned record's id, whichever column carries it.</summary>
    public Guid Target => PinnedId ?? PinnedArtifactId ?? Guid.Empty;

    public static PairedQualificationResultPin For(Guid resultId, DurablePinKind kind, Guid target, DateTimeOffset at) => new()
    {
        Id = Guid.NewGuid(), ResultId = resultId, Kind = kind, PinnedAt = at,
        PinnedId = kind == DurablePinKind.Artifact ? null : target,
        PinnedArtifactId = kind == DurablePinKind.Artifact ? target : null,
    };
}

/// <summary>
/// What kind of record a pin names. Deliberately NOT <see cref="DurableRecordClass"/>: that enum is the set of classes
/// that have a retention RULE, and these are the kinds a qualification result can cite — an agent run has no rule at
/// all (it is identity, never collected) and is pinned anyway, because the other three are reached through it.
/// </summary>
public enum DurablePinKind
{
    LogStream = 1,
    Artifact = 2,
    CleanupReceipt = 3,
    AgentRun = 4,
}
