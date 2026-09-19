using CodeSpace.Messages.Retention;

namespace CodeSpace.Core.Services.Workflows.Retention;

/// <summary>Whether anything still cites one durable record, as far as its cursor can establish it.</summary>
public enum DurableReferenceVerdict
{
    /// <summary>Something still cites the record — a qualification pin, or another holder of the same bytes. Never collect.</summary>
    Referenced = 1,

    /// <summary>Nothing cites the record at ANY enumerated site. A necessary condition for collection, never a sufficient one.</summary>
    Unreferenced = 2,

    /// <summary>The question could not be answered. Read as "keep" everywhere it is consumed.</summary>
    Indeterminate = 3,
}

/// <summary>
/// One record a sweep is considering, in the only terms the generic loop needs: who it is, when its retention clock
/// started, and what an earlier sweep already decided about it. Plane-neutral on purpose — the cursor keeps whatever
/// else it needs to settle the row. <paramref name="Revision"/> is the fence: a settlement applies only while the
/// record still holds the revision the claim saw.
/// </summary>
public sealed record DurableRetentionCandidate(Guid Id, Guid TeamId, long Revision, DateTimeOffset TerminalAt, DateTimeOffset? RetainUntil);

/// <summary>
/// The three instants one sweep measures everything against, computed once by the loop from the class's own rule so a
/// cursor cannot widen its candidate set past the policy.
/// </summary>
/// <param name="Now">The database clock at sweep start. Every deadline compared against it was written by a database clock too.</param>
/// <param name="TerminalBefore">The age floor: a record that went terminal after this is not a candidate at all.</param>
/// <param name="RecheckBefore">The deferral: a record last settled after this was already looked at recently and is left alone.</param>
public sealed record DurableRetentionSweepWindow(DateTimeOffset Now, DateTimeOffset TerminalBefore, DateTimeOffset RecheckBefore);

/// <summary>
/// One plane's answers to the three questions the reaper asks: which records are candidates, does anything still cite
/// this one, and apply this decision. Deliberately narrow (Rule 7): no policy, no clock, no batching — those belong to
/// the loop, so every plane inherits the same waits rather than re-deriving them.
///
/// <para><see cref="ClassifyAsync"/> is fail-closed by contract: any failure to reach a citation site answers
/// <see cref="DurableReferenceVerdict.Indeterminate"/>, never <see cref="DurableReferenceVerdict.Unreferenced"/>.</para>
/// </summary>
public interface IDurableRetentionCursor
{
    DurableRecordClass Class { get; }

    /// <summary>
    /// Records in a terminal state that went terminal at or before <see cref="DurableRetentionSweepWindow.TerminalBefore"/>,
    /// are not already collected, and were not settled since <see cref="DurableRetentionSweepWindow.RecheckBefore"/>.
    /// Oldest first, fairly across tenants, at most <paramref name="limit"/>.
    ///
    /// <para>The recheck half is what keeps one unreclaimable record from owning a batch slot for good: every
    /// settlement — including a keep — advances the record's own modification time, and this query then leaves it
    /// alone until the interval elapses.</para>
    /// </summary>
    Task<IReadOnlyList<DurableRetentionCandidate>> ClaimAsync(DurableRetentionSweepWindow window, int limit, CancellationToken cancellationToken);

    Task<DurableReferenceVerdict> ClassifyAsync(DurableRetentionCandidate candidate, CancellationToken cancellationToken);

    /// <summary>
    /// Applies <paramref name="decision"/> under the candidate's revision fence. False means nothing was settled —
    /// the record moved under this sweep, or the work it needed could not be finished — and the loop reports it as a
    /// keep. A cursor decides for itself whether an unfinished settlement also defers the record; work that is making
    /// progress must NOT, or a drain that needs several passes would take one recheck interval per pass.
    /// </summary>
    Task<bool> SettleAsync(DurableRetentionCandidate candidate, DurableRetentionDecision decision, CancellationToken cancellationToken);
}
