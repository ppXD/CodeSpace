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

/// <summary>
/// Where one artifact's bytes live, expressed as whether and how they can be REMOVED. The reaper derives it; the
/// decision reads it. It is one value rather than a placement plus a sharing flag because the decision's whole
/// question is "is there a purge path for these bytes", and collapsing it here keeps that question answerable from a
/// table of inputs instead of a conjunction the reader has to re-derive.
/// </summary>
public enum ArtifactPurgePath
{
    /// <summary>Bytes live in <c>inline_bytes</c>. Deleting the row deletes them, atomically, with nothing else to do.</summary>
    Inline,

    /// <summary>Bytes are a file on the local blob backend that NO other <c>workflow_artifact</c> row names. Removable.</summary>
    LocalBlobExclusive,

    /// <summary>Bytes are a local blob file that another <c>workflow_artifact</c> row also points at. Not removable: unlinking it would take that row's bytes too, and whether THAT row is collectable is a question this build does not ask.</summary>
    LocalBlobShared,

    /// <summary>Bytes were placed through a configured storage profile. Not removable by this build — see <see cref="ArtifactRetentionDecision.RefuseUnpurgeable"/>.</summary>
    Routed,

    /// <summary>The backend holding the bytes offers no removal at all (it does not implement <c>IArtifactBlobPurge</c>).</summary>
    BackendCannotPurge,

    /// <summary>The placement could not be established. Read as keep everywhere it is consumed.</summary>
    Unknown,
}

/// <summary>Everything one decision is allowed to depend on. A record so the decision function stays a two-argument pure function.</summary>
internal sealed record ArtifactRetentionObservation(ArtifactRetentionState State, DateTimeOffset ArtifactCreatedAt, ArtifactPurgePath Purge, DateTimeOffset? QuarantinedAt, ArtifactReferenceVerdict Verdict, DateTimeOffset Now);

/// <summary>
/// The ONE place that decides whether an artifact may be deleted. Pure, so every safety property is a table of inputs
/// rather than a claim about a distributed system: nothing here reaches a database, a clock or a store.
///
/// <para>Five properties live in this function and nowhere else. An unregistered class keeps. An artifact whose bytes
/// have no purge path keeps — see <see cref="RefuseUnpurgeable"/> for which placements those are and why. An artifact
/// younger than its class's age floor keeps, whatever its reference status. Any verdict other than a definite
/// "unreferenced" keeps. And a first "unreferenced" observation only ever quarantines — collection needs the quarantine
/// window to have elapsed on top of the age floor.</para>
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

        if (RefuseUnpurgeable(observation.Purge) is { } unpurgeable) return unpurgeable;

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

    /// <summary>
    /// The keep-because-the-bytes-cannot-go arm, or null for a placement whose bytes CAN be removed. Shared with the
    /// reaper, which re-asks the same question inside its deleting transaction — one function so the two answers cannot
    /// disagree.
    ///
    /// <para><c>Routed</c> is refused for a reason outside this file, and it is a correctness reason rather than an
    /// unfinished one. A routed object's bytes are reachable by a SECOND route that the retention ledger does not
    /// govern: <c>ArtifactCasRuntimeCoordinator.PutAsync</c> short-circuits on a <c>Committed</c>
    /// <c>artifact_transfer_intent</c> for the content's idempotency scope and returns that intent's object id with no
    /// provider check, and <c>Committed</c> is terminal in SQL (0131's transfer guard whitelists no transition out of
    /// it) while the key generation steps only over <c>Failed</c> intents. So purging the bytes would hand the next
    /// writer of the same content an object whose bytes are gone, and its fresh artifact row could never be read.
    /// Fixing that is a change to the CAS write path's idempotency, not to retention.</para>
    /// </summary>
    public static ArtifactRetentionDecision? RefuseUnpurgeable(ArtifactPurgePath purge) => purge switch
    {
        ArtifactPurgePath.Inline or ArtifactPurgePath.LocalBlobExclusive => null,
        ArtifactPurgePath.LocalBlobShared => Indeterminate("artifact-blob-shared",
            "Another artifact row points at the same physical blob, so removing the bytes would take that row's content too and they are kept."),
        ArtifactPurgePath.Routed => Indeterminate("artifact-routed-storage",
            "The artifact's bytes were placed through a configured storage profile, whose committed transfer intent can hand the same object to a later writer, so this build does not purge them."),
        ArtifactPurgePath.BackendCannotPurge => Indeterminate("artifact-blob-backend-cannot-purge",
            "The blob backend holding the artifact's bytes offers no removal, so the row is kept with them."),
        _ => Retry("artifact-placement-indeterminate", "Where the artifact's bytes live could not be established, so the artifact is kept for now."),
    };

    public static ArtifactRetentionDecision Quarantine(DateTimeOffset collectableAt) => new(ArtifactRetentionAction.Quarantine, null, null, collectableAt);
    public static ArtifactRetentionDecision Collect() => new(ArtifactRetentionAction.Collect, null, null, null);
    public static ArtifactRetentionDecision Referenced() => new(ArtifactRetentionAction.Referenced, null, null, null);
    public static ArtifactRetentionDecision Indeterminate(string code, string message) => new(ArtifactRetentionAction.Indeterminate, code, message, null);
    public static ArtifactRetentionDecision Wait(DateTimeOffset until, string code, string message) => new(ArtifactRetentionAction.Wait, code, message, until);
    public static ArtifactRetentionDecision Retry(string code, string message) => new(ArtifactRetentionAction.Retry, code, message, null);
}
