using CodeSpace.Messages.Retention;

namespace CodeSpace.Core.Services.Workflows.Retention;

/// <summary>What a sweep decided to do with one durable record. Every member except <see cref="Collect"/> is a keep.</summary>
public enum DurableRetentionAction
{
    /// <summary>First observation of "nothing cites this" — record the quarantine deadline, remove nothing.</summary>
    Quarantine,

    /// <summary>Both waits have elapsed and nothing cites the record. The ONLY action that removes anything.</summary>
    Collect,

    /// <summary>Something cites the record. Keep, and clear any quarantine the citation invalidated.</summary>
    Referenced,

    /// <summary>The status cannot be established. Keep.</summary>
    Indeterminate,

    /// <summary>A scheduled wait (an age floor or a quarantine window) that has not elapsed. Keep.</summary>
    Wait,
}

/// <summary>Everything one decision is allowed to depend on. A record so the decision stays a two-argument pure function.</summary>
public sealed record DurableRetentionObservation(DateTimeOffset TerminalAt, DateTimeOffset? RetainUntil, DurableReferenceVerdict Verdict, DateTimeOffset Now);

/// <summary>
/// The ONE place that decides whether a durable record may be reclaimed. Pure, so every safety property is a table of
/// inputs rather than a claim about a distributed system: nothing here reaches a database, a clock or a store.
///
/// <para>Four properties live in this function and nowhere else. An unregistered class keeps. A record younger than
/// its class's age floor keeps, whatever its citation status. Any verdict other than a definite "nothing cites this"
/// keeps. And a first "nothing cites this" observation only ever quarantines — collection needs the quarantine window
/// to have elapsed on top of the age floor, which is a second, independent wait.</para>
///
/// <para><see cref="RetainUntil"/> is not advice: it is the value the record's quarantine marker must hold after the
/// settlement. A citation CLEARS it, which is the property that keeps the two waits independent — when a cited record
/// later stops being cited, its quarantine starts again from that observation rather than inheriting a deadline set
/// while something still pointed at it.</para>
/// </summary>
public sealed record DurableRetentionDecision(DurableRetentionAction Action, string? Code, DateTimeOffset? RetainUntil)
{
    /// <summary>
    /// Decide, from <paramref name="rule"/> (null when the running policy registers no rule for the class) and
    /// <paramref name="observation"/>. Every branch except the last returns a KEEP; reaching
    /// <see cref="DurableRetentionAction.Collect"/> requires passing all of them.
    /// </summary>
    public static DurableRetentionDecision Decide(DurableRetentionRule? rule, DurableRetentionObservation observation)
    {
        ArgumentNullException.ThrowIfNull(observation);

        if (rule is null) return Indeterminate("retention-class-unregistered", observation.RetainUntil);

        var eligibleAt = observation.TerminalAt.Add(rule.MinimumAge);

        if (observation.Now < eligibleAt) return Wait("age-floor-open", observation.RetainUntil);

        if (observation.Verdict == DurableReferenceVerdict.Referenced) return Referenced();

        if (observation.Verdict != DurableReferenceVerdict.Unreferenced) return Indeterminate("reference-status-indeterminate", observation.RetainUntil);

        if (observation.RetainUntil is not { } retainUntil) return Quarantine(observation.Now.Add(rule.QuarantineWindow));

        return observation.Now >= retainUntil ? Collect(retainUntil) : Wait("quarantine-window-open", retainUntil);
    }

    public static DurableRetentionDecision Quarantine(DateTimeOffset retainUntil) => new(DurableRetentionAction.Quarantine, null, retainUntil);
    public static DurableRetentionDecision Collect(DateTimeOffset retainUntil) => new(DurableRetentionAction.Collect, null, retainUntil);

    /// <summary>A citation. The quarantine marker is cleared, because the observation it recorded has been contradicted.</summary>
    public static DurableRetentionDecision Referenced() => new(DurableRetentionAction.Referenced, null, null);

    public static DurableRetentionDecision Indeterminate(string code, DateTimeOffset? retainUntil) => new(DurableRetentionAction.Indeterminate, code, retainUntil);
    public static DurableRetentionDecision Wait(string code, DateTimeOffset? retainUntil) => new(DurableRetentionAction.Wait, code, retainUntil);
}
