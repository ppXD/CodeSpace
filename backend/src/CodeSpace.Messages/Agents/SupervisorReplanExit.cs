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
/// <para>THE ARMS SPLIT ON WHAT THE TAPE CAN ACTUALLY WITNESS, which is the whole reason there is more than one.
/// A plan authored after a verdict that nothing has re-graded since has NOT been shown to change nothing — the
/// observed loop's very next honest move is to STAGE it (<see cref="ToStaging"/>, or
/// <see cref="ToStagingBehindADependency"/> when the dependency rail defers that staging), and telling that tape
/// "another plan changes nothing, propose an amendment" would withdraw the plan verb one turn after the model
/// correctly authored one and name no verb that could ever grade the repaired check. Only a unit RE-GRADED under a
/// new plan that came back with the identical verdict is evidence a re-plan changed nothing, and only that unit is
/// sent at the oracle (<see cref="ToAmendment"/>) or at a human (<see cref="ToHuman"/>).</para>
///
/// <para>The re-graded reading is over ANY plan boundary the unit was re-graded across, never only the newest one,
/// and it WINS over the staging arms. Scoped to the newest generation it had a hole the size of the loop it closes:
/// on a tape whose re-grade came back identical, the model's authoring one MORE plan moved the boundary past the
/// evidence, the reading fell back to "a plan is authored and unrun", and <c>spawn → identical re-grade → amend →
/// plan → …</c> ran as a longer cycle of the same fixed point. The tape's memory that a re-plan already failed to
/// move this verdict must not be erasable by authoring another one.</para>
///
/// <para>Every live exit but <see cref="ToHuman"/> additionally requires the NEWEST plan generation to still
/// declare the unit: a co-sign minted for a unit the current plan dropped can never be consumed by a retry, and a
/// spawn cannot stage a unit the plan does not declare. A dropped unit's only exit is a human ruling.</para>
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

    /// <summary>
    /// <see cref="ToStaging"/>'s tape, with the dependency rail across it: the re-declared unit is still waiting on
    /// a <c>DependsOn</c> the CURRENT generation has not satisfied, so the spawn the plain staging arm names would
    /// be clamped and stage nothing (<c>SupervisorDependencyGate.Partition</c> defers it, and an all-deferred spawn
    /// is accepted-empty). The exit is still the staging — never another plan — but ORDERED: spawn what it waits on
    /// first. Its own arm rather than a footnote on <see cref="ToStaging"/>, because the two copies differ in the
    /// one place a model reads for its verb, and the frontier block one screen away already contradicts the
    /// unqualified sentence.
    /// </summary>
    ToStagingBehindADependency = 4,
}
