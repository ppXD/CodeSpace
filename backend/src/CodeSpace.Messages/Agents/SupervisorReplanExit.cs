namespace CodeSpace.Messages.Agents;

/// <summary>
/// Where a unit whose acceptance verdict a RE-PLAN already failed to move must be sent instead — the reading both
/// re-plan steers in the decider's results block substitute their own "re-plan this item" sentence with.
///
/// <para>It exists because <c>plan</c> is deliberately never masked (it is one of the three escape hatches), so a
/// steer that names it is a standing invitation the server will always accept. On a unit whose verdict a re-plan
/// cannot change, accepting it changes nothing, the identical steer re-renders next turn, and the run walks the
/// fixed point into its no-progress kill: the observed <c>plan→spawn→plan×6→stop</c>, ~25-40% of the attempts of
/// the conflict arm <c>The_real_model_observes_a_real_conflict_and_chooses_to_resolve</c> (runs 34104701023 and
/// 34101026801 attempt 2). <see cref="SupervisorAmendStanding.Discarded"/> is the same defect with a human's
/// co-sign in it, and had an exit ramp first; this is the reading for the far commoner tape that never reached a
/// co-sign at all.</para>
///
/// <para>The two non-None arms differ ONLY in whether <c>amend_acceptance</c> is admissible for the unit, which is
/// a server verdict (<c>SupervisorAmendPrecondition</c>) and the same one the turn's roster reads: a steer that
/// named the verb where the roster withholds it would be the two-rosters defect again, one screen apart.</para>
/// </summary>
public enum SupervisorReplanExit
{
    /// <summary>No re-plan has left this unit's verdict unchanged — the re-plan steers read exactly as they did before this distinction existed.</summary>
    None = 0,

    /// <summary>A re-plan already left this verdict unchanged AND the server's amend gate admits a proposal for the unit (its check could not RUN) — the exit is <c>amend_acceptance</c>, with <c>ask_human</c> behind it.</summary>
    ToAmendment = 1,

    /// <summary>A re-plan already left this verdict unchanged and no amendment is admissible — the check RAN and rejected the WORK, so there is no oracle to repair and a human ruling is the only exit left.</summary>
    ToHuman = 2,
}
