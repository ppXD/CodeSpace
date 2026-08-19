namespace CodeSpace.Messages.Contracts;

/// <summary>
/// Q4 (SOTA-claim gate): the ONE lawful statement of a (mode, capability) pair's measured performance standing,
/// resolved from the qualification-receipt ledger at read time — never from a committed constant. <c>Sealed</c>
/// stands only while a current (effective, unexpired, unrevoked) sealed receipt backs it, and the claim carries
/// that receipt's identity so every rendered number is auditable back to the round that earned it.
/// </summary>
public sealed record PerformanceClaim
{
    public required string Mode { get; init; }

    public required string CapabilityKey { get; init; }

    /// <summary>The standing the backing receipt grants, verbatim — <c>Unmeasured</c> when nothing current backs the pair.</summary>
    public required PerformanceQualification Performance { get; init; }

    /// <summary>The backing receipt — null exactly when <see cref="Performance"/> is Unmeasured.</summary>
    public Guid? ReceiptId { get; init; }

    /// <summary>The hidden-suite digest the backing round was measured against — pins WHICH tasks earned the standing.</summary>
    public string? SuiteDigest { get; init; }

    /// <summary>When the backing claim lapses — re-qualification is owed by then.</summary>
    public DateTimeOffset? ExpiresAt { get; init; }

    /// <summary>Layer 1 of the backing round's identity: WHICH cohort the standing covers — null when nothing backs the pair or the backing receipt predates the typed noun.</summary>
    public LaunchCohortDescriptor? Cohort { get; init; }

    /// <summary>Layer 2: WHAT was measured and WHO judged it — null exactly when <see cref="Performance"/> is Unmeasured.</summary>
    public ContractSeal? Seal { get; init; }
}

/// <summary>The full claim board — every registered (mode × capability) pair's standing at <see cref="AsOf"/>, ordered (mode, capability) ordinal.</summary>
public sealed record QualificationClaimBoard
{
    public required DateTimeOffset AsOf { get; init; }

    public required IReadOnlyList<PerformanceClaim> Rows { get; init; }
}
