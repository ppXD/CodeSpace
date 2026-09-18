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
/// else it needs to settle the row.
/// </summary>
public sealed record DurableRetentionCandidate(Guid Id, Guid TeamId, long Revision, DateTimeOffset TerminalAt, DateTimeOffset? RetainUntil);

/// <summary>
/// One plane's answers to the three questions the reaper asks: which records are candidates, does anything still cite
/// this one, and apply this decision. Deliberately narrow (Rule 7): no policy, no clock, no batching — those belong to
/// the loop, so every plane inherits the same waits rather than re-deriving them.
///
/// <para><see cref="ClassifyAsync"/> is fail-closed by contract: any failure to reach a citation site answers
/// <see cref="DurableReferenceVerdict.Indeterminate"/>, never <see cref="DurableReferenceVerdict.Unreferenced"/>.
/// <see cref="SettleAsync"/> returns false when the record moved under the sweep, which costs nothing — the next
/// sweep meets a row this one never held.</para>
/// </summary>
public interface IDurableRetentionCursor
{
    DurableRecordClass Class { get; }

    /// <summary>
    /// Records in a terminal state that went terminal at or before <paramref name="terminalBefore"/> (the class's age
    /// floor, computed by the loop), are not already collected, and are not deferred past <paramref name="now"/>.
    /// Oldest first, at most <paramref name="limit"/>.
    ///
    /// <para>The deferral half is what keeps one unreclaimable record from owning a batch slot forever: a sweep that
    /// cannot finish pushes the record's own retention deadline forward, and this query then skips it until then.</para>
    /// </summary>
    Task<IReadOnlyList<DurableRetentionCandidate>> ClaimAsync(DateTimeOffset now, DateTimeOffset terminalBefore, int limit, CancellationToken cancellationToken);

    Task<DurableReferenceVerdict> ClassifyAsync(DurableRetentionCandidate candidate, CancellationToken cancellationToken);

    /// <summary>Applies <paramref name="decision"/>. Only <see cref="DurableRetentionAction.Quarantine"/> and <see cref="DurableRetentionAction.Collect"/> write anything; every other action is a keep with nothing to record.</summary>
    Task<bool> SettleAsync(DurableRetentionCandidate candidate, DurableRetentionDecision decision, CancellationToken cancellationToken);
}
