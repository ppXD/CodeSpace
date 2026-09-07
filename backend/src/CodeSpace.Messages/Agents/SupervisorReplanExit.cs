namespace CodeSpace.Messages.Agents;

/// <summary>
/// Where a unit a RE-PLAN has already been authored over must be sent instead — the reading both re-plan steers in
/// the decider's results block substitute their own "re-plan this item" sentence with.
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
/// <para>THE ARMS SPLIT ON WHAT THE TAPE CAN ACTUALLY WITNESS, which is the whole reason there are three of them.
/// A plan authored after a verdict that nothing has re-graded since has NOT been shown to change nothing — the
/// observed loop's very next honest move is to STAGE it (<see cref="ToStaging"/>), and telling that tape "another
/// plan changes nothing, propose an amendment" would withdraw the plan verb one turn after the model correctly
/// authored one and name no verb that could ever grade the repaired check. Only a unit RE-GRADED under the new plan
/// that came back with the identical verdict is evidence the re-plan changed nothing, and only that unit is sent at
/// the oracle (<see cref="ToAmendment"/>) or at a human (<see cref="ToHuman"/>).</para>
///
/// <para><see cref="ToAmendment"/> and <see cref="ToStaging"/> additionally require the NEWEST plan generation to
/// still declare the unit: a co-sign minted for a unit the current plan dropped can never be consumed by a retry,
/// and a spawn cannot stage a unit the plan does not declare. A dropped unit's only exit is a human ruling.</para>
///
/// <para>Which of <see cref="ToAmendment"/> / <see cref="ToHuman"/> a re-graded unit gets is a server verdict
/// (<c>SupervisorAmendPrecondition</c>) and the same one the turn's roster reads: a steer that named
/// <c>amend_acceptance</c> where the roster withholds it would be the two-rosters defect again, one screen
/// apart.</para>
/// </summary>
public enum SupervisorReplanExit
{
    /// <summary>No plan has been authored over this unit's standing verdict — the re-plan steers read exactly as they did before this distinction existed.</summary>
    None = 0,

    /// <summary>A re-plan was RE-GRADED and returned the identical verdict AND the server's amend gate admits a proposal for the unit (its check could not RUN) — the exit is <c>amend_acceptance</c>, with <c>ask_human</c> behind it.</summary>
    ToAmendment = 1,

    /// <summary>A plan was authored over this verdict and no exit that could move it remains: either the re-graded verdict is a WORK rejection with no oracle to repair, or the newest plan no longer declares the unit at all. A human ruling is the only door left.</summary>
    ToHuman = 2,

    /// <summary>A plan for this unit was authored AFTER its standing verdict and nothing has been staged under it since — the re-plan is not spent yet, it is unrun. The exit is <c>spawn</c>: stage the plan the run already has, and do not author a second one over the same verdict.</summary>
    ToStaging = 3,
}
