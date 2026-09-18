namespace CodeSpace.Messages.Retention;

/// <summary>
/// A class of durable record that has a retention rule. Membership is the same test the artifact plane applies to
/// <c>ArtifactRetentionClass</c>: a class exists only when the COMPLETE set of places that can still cite one of its
/// records is enumerable — a column, or a pin written in the citing statement's own transaction. A record whose
/// citations can also reach a JSON payload has no class and is therefore never a reap candidate.
/// </summary>
public enum DurableRecordClass
{
    /// <summary>An <c>agent_run_log_stream</c> head and the routed CAS bytes its segments name.</summary>
    LogStream = 1,

    /// <summary>An <c>agent_run_cleanup_receipt</c> row (migration 0229).</summary>
    CleanupReceipt = 2,

    /// <summary>A <c>workflow_run_capture_gap</c> row (migration 0231). Kept exactly as long as the stream whose missing span it describes.</summary>
    CaptureGap = 3,

    /// <summary>The paired-qualification evidence tables (migrations 0218–0225) behind a sealed result.</summary>
    QualificationEvidence = 4,

    /// <summary>A <c>budget_reservation</c> row (migration 0104) that has already been reconciled.</summary>
    BudgetReservation = 5,

    /// <summary>An <c>artifact_transfer_intent</c> row (migration 0226) that reached a terminal state still holding a staging key.</summary>
    TransferIntent = 6,
}

/// <summary>
/// One class's committed rule. <paramref name="MinimumAge"/> is the age floor measured from the record's own terminal
/// instant: below it the record is not even considered, so a citation that is still in flight cannot be outrun.
/// <paramref name="QuarantineWindow"/> is the second, independent wait measured from the first observation of
/// "nothing cites this" — collection needs BOTH to have elapsed.
/// </summary>
public sealed record DurableRetentionRule(DurableRecordClass Class, TimeSpan MinimumAge, TimeSpan QuarantineWindow);

/// <summary>What one bounded sweep did. Every claimed record lands in exactly one of these buckets.</summary>
public sealed record DurableRetentionSweepSummary
{
    public required int Claimed { get; init; }

    /// <summary>First observation of "nothing cites this": the quarantine deadline was recorded and nothing was removed.</summary>
    public required int Quarantined { get; init; }

    /// <summary>Both waits elapsed with no citation. The only bucket that removed anything.</summary>
    public required int Collected { get; init; }

    /// <summary>Something still cites the record. Kept.</summary>
    public required int Referenced { get; init; }

    /// <summary>The question could not be answered, or the removal could not be completed. Kept.</summary>
    public required int Indeterminate { get; init; }

    /// <summary>A scheduled wait — an age floor or a quarantine window that has not elapsed. Kept, and re-asked next sweep.</summary>
    public required int Waiting { get; init; }

    public static DurableRetentionSweepSummary Empty { get; } =
        new() { Claimed = 0, Quarantined = 0, Collected = 0, Referenced = 0, Indeterminate = 0, Waiting = 0 };
}
