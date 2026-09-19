namespace CodeSpace.Messages.Retention;

/// <summary>
/// A class of durable record that has a retention rule AND a cursor that can act on it. A class is added here together
/// with the cursor that sweeps it, never ahead of one: a rule with no cursor reclaims nothing and reads, to anyone
/// scanning this file, as a promise the system does not keep.
///
/// <para>Membership is the same test the artifact plane applies to <c>ArtifactRetentionClass</c>: a class exists only
/// when the COMPLETE set of places that can still cite one of its records is enumerable — a column, or a pin written in
/// the citing statement's own transaction. A record whose citations can also reach a JSON payload has no class and is
/// therefore never a reap candidate.</para>
/// </summary>
public enum DurableRecordClass
{
    /// <summary>An <c>agent_run_log_stream</c> head and the routed CAS bytes its segments name.</summary>
    LogStream = 1,
}

/// <summary>
/// One class's committed rule. <paramref name="MinimumAge"/> is the age floor measured from the record's own terminal
/// instant: below it the record is not even considered, so a citation that is still in flight cannot be outrun.
/// <paramref name="QuarantineWindow"/> is the second, independent wait measured from the first observation of
/// "nothing cites this" — collection needs BOTH to have elapsed. <paramref name="RecheckInterval"/> is neither: it is
/// how long a record that was looked at and KEPT is left alone, so one record nothing can ever reclaim cannot own a
/// batch slot for good.
/// </summary>
public sealed record DurableRetentionRule(DurableRecordClass Class, TimeSpan MinimumAge, TimeSpan QuarantineWindow, TimeSpan RecheckInterval);

/// <summary>What one bounded sweep did. Every claimed record lands in exactly one of these buckets.</summary>
public sealed record DurableRetentionSweepSummary
{
    public required int Claimed { get; init; }

    /// <summary>First observation of "nothing cites this": the quarantine deadline was recorded and nothing was removed.</summary>
    public required int Quarantined { get; init; }

    /// <summary>Both waits elapsed with no citation. The only bucket that removed anything.</summary>
    public required int Collected { get; init; }

    /// <summary>Something still cites the record. Kept, and not looked at again until the recheck interval elapses.</summary>
    public required int Referenced { get; init; }

    /// <summary>
    /// A keep this sweep could not turn into a quarantine or a collection: an unanswered citation question, a refused
    /// removal, a drain that needs another pass, or a record whose bytes were already gone by some other path. Kept.
    ///
    /// <para>Distinguishing this from <see cref="Collected"/> is the point of having it: a sweep that reports Claimed
    /// and nothing else is a sweep that is re-asking, and an operator reading these numbers has to be able to see
    /// that rather than infer healthy work from a non-zero claim count.</para>
    /// </summary>
    public required int Kept { get; init; }

    public static DurableRetentionSweepSummary Empty { get; } =
        new() { Claimed = 0, Quarantined = 0, Collected = 0, Referenced = 0, Kept = 0 };
}
