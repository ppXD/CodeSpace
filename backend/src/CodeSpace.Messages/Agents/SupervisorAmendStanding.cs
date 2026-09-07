namespace CodeSpace.Messages.Agents;

/// <summary>
/// WHERE one unit stands against the co-signed acceptance amendments on its own tape — the single fact the infra
/// verdict's steer is allowed to name a verb from. Derived by <c>SupervisorAmendObligation.StandingFor</c>, the
/// SAME walk the outstanding-amendment banner reads, so the per-unit results block and the banner cannot say
/// opposite things about the same subtask in one prompt.
///
/// <para>The live miss this exists for (main c77bcfa15, run 34066916864, arm
/// <c>The_real_model_repairs_a_broken_oracle_through_the_cosign_loop</c>): a human co-signed two amendments and the
/// retries followed, yet the infra verdict for those units kept reading "Do NOT retry the agent … Re-plan this item
/// with a check its agent can satisfy". The brain re-planned eight times into the no-progress kill — and a re-plan
/// DISCARDS every approved amendment, because amendments anchor to the newest plan (the MAJOR-8 rule the co-sign
/// overlay and the retry obligation both apply). Nothing in the prompt named that cost. Four states, because only
/// four are materially different.</para>
/// </summary>
public enum SupervisorAmendStanding
{
    /// <summary>No approved REPLACEMENT amendment for this unit survives on the current plan — the infra verdict steers exactly as it did before this distinction existed (re-plan the check, or ask a human to rule).</summary>
    None = 0,

    /// <summary>An approved amendment for this unit is NOT yet consumed: no staging decision named it after the co-sign, so its recorded verdict was graded by the DEAD oracle. The retry is owed, and a re-plan would throw the human's ruling away.</summary>
    AwaitingRetry = 1,

    /// <summary>An approved amendment for this unit was already consumed by a later staging — the amended check has had its pass. A check that still cannot run earns a second co-sign or a human ruling, never a re-plan, which discards the first one for nothing.</summary>
    Consumed = 2,

    /// <summary>
    /// An approved amendment for this unit exists on the tape but PREDATES the newest plan — a re-plan already
    /// threw it away (MAJOR-8), and the unit is back on the check that could not run.
    ///
    /// <para>Materially different from <see cref="None"/>, and the distinction is the whole loop: a never-co-signed
    /// unit is honestly steered at authoring a satisfiable check, whereas THIS unit has already had one authored and
    /// co-signed, and re-planning is precisely what lost it. Answering it with another plan is the step that made
    /// run 34066916864 spend eight turns re-discarding the same two rulings — so the steer sends it back to
    /// <c>amend_acceptance</c> (which re-anchors the repair to the current plan) or to a human.</para>
    /// </summary>
    Discarded = 3,
}
