namespace CodeSpace.Messages.Agents;

/// <summary>
/// WHICH landing move the tape still leaves reachable — the one fact the stopped-now recital's steer is allowed to
/// name a verb from. Derived by <c>SupervisorActionMask.LandingReachFor</c> off the SAME conflict-presence authority
/// and resolve budget the mask itself masks <c>resolve</c> on, so the prompt can never offer a verb the mask forbids
/// in the same breath.
///
/// <para>The live miss this exists for (main 64f80f07, run 34027621996): with a conflicted integration recorded and
/// the resolve cap SPENT, the recital's steer still read "Land that work" while <c>merge</c> was the only landing
/// verb left — and a merge there just repeats the conflicted integration. The decider's own resolution-verdict copy
/// said the opposite ("'stop' and leave the conflict for a human, or 'ask_human'") in the same prompt, and the model
/// followed the newer line. Three states, because only three are materially different.</para>
/// </summary>
public enum SupervisorLandingReach
{
    /// <summary>Nothing on the tape narrows the landing move — no conflicted integration is recorded. The steer names no specific verb ("Land that work"), exactly as it did before this distinction existed.</summary>
    Unconstrained = 0,

    /// <summary>A conflicted integration IS recorded and <c>resolve</c> can still run within the cap — reconciling it is the landing move, and the steer says so rather than leaving the model to guess at <c>merge</c>.</summary>
    ReconcileFirst = 1,

    /// <summary>A conflicted integration is recorded and a further <c>resolve</c> would FORCE-STOP the run (the cap is spent). No landing verb is reachable at all, so the steer offers the honest exits ALONE — naming one here is what sent run 34027621996 into a blind re-merge.</summary>
    NoLandingReachable = 2,
}
