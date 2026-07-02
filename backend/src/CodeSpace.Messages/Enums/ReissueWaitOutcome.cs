namespace CodeSpace.Messages.Enums;

/// <summary>
/// The outcome of an operator's request to force-reissue a stranded pending wait. Distinguishes the three non-404
/// cases the caller maps to a response: a genuine reissue, a no-op because the wait already resolved (a real signal /
/// deadline / concurrent reissue won the CAS), and a refusal because the wait is not operator-reissuable. A foreign /
/// absent run or wait is a 404 (a thrown <c>KeyNotFoundException</c>), never one of these.
/// </summary>
public enum ReissueWaitOutcome
{
    /// <summary>The stranded signal-driven wait was force-resolved and the run re-dispatched.</summary>
    Reissued,

    /// <summary>The wait was no longer Pending — a real signal, the deadline, or a concurrent reissue resolved it first. Idempotent no-op.</summary>
    AlreadyResolved,

    /// <summary>The wait is not operator-reissuable (a decision- or completion-driven kind); it resolves via its own verb or a reconciler backstop, not this override.</summary>
    UnsupportedKind,
}
