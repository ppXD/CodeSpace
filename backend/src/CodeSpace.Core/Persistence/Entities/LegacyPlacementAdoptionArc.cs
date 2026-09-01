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

public enum LegacyPlacementAdoptionPassOutcome
{
    Advanced,
    Retryable,
    Aborted,
    Interrupted,
}

public enum LegacyPlacementAdoptionYieldReason
{
    None,
    RowLimit,
    ByteBudget,
    TimeBudget,
    ProviderRetryable,
}

public enum LegacyPlacementAdoptionPassFailureCode
{
    None,
    ProviderTransient,
    ProviderRejected,
    ProgrammingFault,
    CallerCancelled,
    CursorStale,
    AdmissionEvidenceMissing,
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
    public DateTimeOffset? ClaimStartedAt { get; set; }
    public DateTimeOffset? ClaimExpiresAt { get; set; }
    public DateTimeOffset? SealedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset LastModifiedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }

    /// <summary>Object-shaped terminal replay payload, recorded before Cleaning starts and immutable thereafter.</summary>
    public string? FinalSummaryJson { get; set; }
    /// <summary>
    /// Version one makes the cumulative counters and pass ledger authoritative. Version zero is wholly incomplete:
    /// it may retain a strict prefix of counters/audit rows after a rolling-deploy downgrade, and consumers must not
    /// infer completeness from their presence or sums.
    /// </summary>
    public short AuditVersion { get; set; }
    public long EvidenceExamined { get; set; }
    public long EvidenceResolved { get; set; }
    public long EvidenceConfirmed { get; set; }
    public long MintExamined { get; set; }
    public long Available { get; set; }
    public long Missing { get; set; }
    public long Corrupt { get; set; }
    public long AlreadyRecorded { get; set; }
    public long Conflicts { get; set; }
    public long Retryable { get; set; }
    public long ReadBytes { get; set; }
    public long CompletedPasses { get; set; }
    public long BudgetYields { get; set; }
    public long OversizedPasses { get; set; }
    public Guid? LastSettledClaimToken { get; set; }
    public uint Xmin { get; set; }

    public Team Team { get; set; } = default!;
    public StorageProfile StorageProfile { get; set; } = default!;
    public StorageProfileRevision StorageProfileRevision { get; set; } = default!;
    public ICollection<LegacyPlacementAdoptionMember> Members { get; set; } = [];
    public ICollection<LegacyPlacementAdoptionPassAudit> PassAudits { get; set; } = [];
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

/// <summary>One secret-free append-only outcome for one claimed provider pass. Renewals and unclaimed cleanup never create rows.</summary>
public sealed class LegacyPlacementAdoptionPassAudit
{
    public Guid ArcId { get; set; }
    public Guid ClaimToken { get; set; }
    public LegacyPlacementAdoptionArcPhase Phase { get; set; }
    public LegacyPlacementAdoptionPassOutcome Outcome { get; set; }
    public LegacyPlacementAdoptionYieldReason YieldReason { get; set; }
    public LegacyPlacementAdoptionPassFailureCode FailureCode { get; set; }
    public long StartPosition { get; set; }
    public long EndPosition { get; set; }
    public long Examined { get; set; }
    public long Resolved { get; set; }
    public long Confirmed { get; set; }
    public long EvidenceExaminedDelta { get; set; }
    public long EvidenceResolvedDelta { get; set; }
    public long EvidenceConfirmedDelta { get; set; }
    public long MintExaminedDelta { get; set; }
    public long AvailableDelta { get; set; }
    public long MissingDelta { get; set; }
    public long CorruptDelta { get; set; }
    public long AlreadyRecordedDelta { get; set; }
    public long ConflictsDelta { get; set; }
    public long RetryableDelta { get; set; }
    public long ReadBytesDelta { get; set; }
    public bool OversizedItem { get; set; }
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset CompletedAt { get; set; }

    public LegacyPlacementAdoptionArc Arc { get; set; } = default!;
}
