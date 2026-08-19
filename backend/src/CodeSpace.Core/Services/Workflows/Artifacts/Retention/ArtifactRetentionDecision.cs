using CodeSpace.Messages.Artifacts;

namespace CodeSpace.Core.Services.Workflows.Artifacts.Retention;

/// <summary>What a sweep decided to do with one declaration.</summary>
internal enum ArtifactRetentionAction
{
    /// <summary>First observation of "unreferenced" — start the quarantine clock, delete nothing.</summary>
    Quarantine,

    /// <summary>Both waits have elapsed and no reference exists. The ONLY action that deletes.</summary>
    Collect,

    /// <summary>A reference exists. Terminal keep.</summary>
    Referenced,

    /// <summary>The status cannot be established. Terminal keep.</summary>
    Indeterminate,

    /// <summary>A scheduled wait (an age floor or a quarantine window). Re-queued without spending the retry budget.</summary>
    Wait,

    /// <summary>A transient failure. Re-queued, and it spends the retry budget so a permanent failure ends as Indeterminate.</summary>
    Retry,
}

/// <summary>Everything one decision is allowed to depend on. A record so the decision function stays a two-argument pure function.</summary>
internal sealed record ArtifactRetentionObservation(ArtifactRetentionState State, DateTimeOffset ArtifactCreatedAt, bool Inline, DateTimeOffset? QuarantinedAt, ArtifactReferenceVerdict Verdict, DateTimeOffset Now);

/// <summary>
/// The ONE place that decides whether an artifact may be deleted. Pure, so every safety property is a table of inputs
/// rather than a claim about a distributed system: nothing here reaches a database, a clock or a store.
///
/// <para>Five properties live in this function and nowhere else. An unregistered class keeps. A non-inline artifact
/// keeps, because no purge path exists for bytes outside the row. An artifact younger than its class's age floor keeps,
/// whatever its reference status. Any verdict other than a definite "unreferenced" keeps. And a first "unreferenced"
/// observation only ever quarantines — collection needs the quarantine window to have elapsed on top of the age
/// floor.</para>
/// </summary>
internal sealed record ArtifactRetentionDecision(ArtifactRetentionAction Action, string? ErrorCode, string? ErrorMessage, DateTimeOffset? NextSweepAt)
{
    /// <summary>
    /// Decide, from <paramref name="rule"/> (null when the running policy registers no rule for the declaration's class)
    /// and <paramref name="observation"/>. Every branch except the last returns a KEEP; reaching
    /// <see cref="ArtifactRetentionAction.Collect"/> requires passing all of them.
    /// </summary>
    public static ArtifactRetentionDecision Decide(ArtifactRetentionRule? rule, ArtifactRetentionObservation observation)
    {
        ArgumentNullException.ThrowIfNull(observation);

        if (rule is null)
            return Indeterminate("retention-class-unregistered", "The running retention policy registers no rule for this declaration's class.");

        if (!observation.Inline)
            return Indeterminate("artifact-not-inline", "The artifact's bytes live outside the row and no purge path exists for them, so the row is kept.");

        var eligibleAt = observation.ArtifactCreatedAt.Add(rule.MinimumAge);

        if (observation.Now < eligibleAt)
            return Wait(eligibleAt, "age-floor-open", "The artifact has not yet reached its class's age floor.");

        if (observation.Verdict == ArtifactReferenceVerdict.Referenced) return Referenced();

        if (observation.Verdict != ArtifactReferenceVerdict.Unreferenced)
            return Retry("reference-status-indeterminate", "A reference site could not be probed, so the artifact is kept for now.");

        if (observation.QuarantinedAt is not { } quarantinedAt)
            return Quarantine(observation.Now.Add(rule.QuarantineWindow));

        var collectableAt = quarantinedAt.Add(rule.QuarantineWindow);

        return observation.Now >= collectableAt
            ? Collect()
            : Wait(collectableAt, "quarantine-window-open", "The quarantine window since the first unreferenced observation has not elapsed.");
    }

    public static ArtifactRetentionDecision Quarantine(DateTimeOffset collectableAt) => new(ArtifactRetentionAction.Quarantine, null, null, collectableAt);
    public static ArtifactRetentionDecision Collect() => new(ArtifactRetentionAction.Collect, null, null, null);
    public static ArtifactRetentionDecision Referenced() => new(ArtifactRetentionAction.Referenced, null, null, null);
    public static ArtifactRetentionDecision Indeterminate(string code, string message) => new(ArtifactRetentionAction.Indeterminate, code, message, null);
    public static ArtifactRetentionDecision Wait(DateTimeOffset until, string code, string message) => new(ArtifactRetentionAction.Wait, code, message, until);
    public static ArtifactRetentionDecision Retry(string code, string message) => new(ArtifactRetentionAction.Retry, code, message, null);
}
