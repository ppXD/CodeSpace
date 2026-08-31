namespace CodeSpace.Core.Persistence.Entities;

public enum LegacyPlacementAdoptionArcPhase
{
    Evidence,
    Minting,
    Cleaning,
}

public enum LegacyPlacementAdoptionArcState
{
    Active,
    Cleaning,
    Completed,
    Expired,
    Stale,
}

/// <summary>
/// One durable, team-wide adoption population. Members are a closed control-plane snapshot; this row is its lease,
/// page fence, and compact terminal tombstone. It never links a runtime artifact reader to CAS.
/// </summary>
public sealed class LegacyPlacementAdoptionArc
{
    public Guid Id { get; set; }
    public Guid TeamId { get; set; }
    public Guid StorageProfileId { get; set; }
    public Guid StorageProfileRevisionId { get; set; }
    public int ProfileRevision { get; set; }
    public Guid CreatedBy { get; set; }
    public LegacyPlacementAdoptionArcPhase Phase { get; set; }
    public LegacyPlacementAdoptionArcState State { get; set; }
    public LegacyPlacementAdoptionArcState? TerminalState { get; set; }
    /// <summary>The smallest confirmed (size, position) copied source identity; not a retention reference.</summary>
    public Guid? WitnessSourceWorkflowRowId { get; set; }
    public long CurrentPosition { get; set; }
    public long MemberCount { get; set; }
    public long Revision { get; set; }
    public Guid? ClaimToken { get; set; }
    public DateTimeOffset? ClaimExpiresAt { get; set; }
    public DateTimeOffset? SealedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset LastModifiedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }

    /// <summary>Object-shaped terminal replay payload, recorded before Cleaning starts and immutable thereafter.</summary>
    public string? FinalSummaryJson { get; set; }
    public uint Xmin { get; set; }

    public Team Team { get; set; } = default!;
    public StorageProfile StorageProfile { get; set; } = default!;
    public StorageProfileRevision StorageProfileRevision { get; set; } = default!;
    public ICollection<LegacyPlacementAdoptionMember> Members { get; set; } = [];
}

/// <summary>A copied immutable source identity, not a reference. Deliberately no FK: retention may delete its source.</summary>
public sealed class LegacyPlacementAdoptionMember
{
    public Guid ArcId { get; set; }

    /// <summary>Database-allocated keyset position. It is an arc-local ordering key only through the composite PK.</summary>
    public long Position { get; set; }
    public Guid SourceWorkflowRowId { get; set; }
    public DateTimeOffset SourceCreatedAt { get; set; }
    public string Sha256 { get; set; } = default!;
    public long SizeBytes { get; set; }
    public string StorageUrl { get; set; } = default!;

    public LegacyPlacementAdoptionArc Arc { get; set; } = default!;
}
