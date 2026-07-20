namespace CodeSpace.Messages.Contracts;

/// <summary>
/// The SEALED six-state terminal vocabulary (v4.2-FINAL Lock Clause 5) — what a run's completion HONESTLY is,
/// decided from the five-dimensional <see cref="CompletionAssessment"/> plus the handoff-reachability fact by the
/// ONE pure decider (<c>TerminalDecider</c>). <c>IsTerminalizable</c> means only "a terminal can be RECORDED" —
/// it never by itself means success. Exactly ONE state is VDS-eligible: <see cref="CleanSuccess"/>; every other
/// state is an honest non-success terminal, and CaptureFailed / PolicyBlocked / WaivedByPolicy dispositions can
/// never reach it. P3b builds this decider INACTIVE (shadow-recorded only); production terminal mutation has
/// exactly one owner and it is P2b (Lock Clause 1).
/// </summary>
public enum TerminalDecision
{
    /// <summary>Solved ∧ Verification positive/authorized-NA ∧ Artifact captured/authorized-NothingExpected ∧ Delivery delivered/genuinely-NotRequired ∧ Handoff reachable. The ONLY VDS-eligible state.</summary>
    CleanSuccess,

    /// <summary>The run honestly did not solve it (unsolved outcome, failed capture, forced stop, give-up) — recorded as failure, never inflated.</summary>
    HonestFailure,

    /// <summary>The asked capability is not qualified/supported in this mode — an honest "cannot do this here", never a fake attempt.</summary>
    Unsupported,

    /// <summary>The model abstained pending input only the user can give — the ask returns to the human, not a failure and never a success.</summary>
    NeedsClarification,

    /// <summary>An obligation is unsettled (Unknown dimension, waiver pending its co-sign arc, contradictory facts) — a human must look before any claim stands.</summary>
    NeedsReview,

    /// <summary>The work stands but its arrival is policy-blocked/waived or its handoff is unreachable — parked for the human to retrieve/continue (park, don't die).</summary>
    Park,
}
