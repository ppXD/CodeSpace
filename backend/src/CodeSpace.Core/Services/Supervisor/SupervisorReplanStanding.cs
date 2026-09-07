using CodeSpace.Messages.Agents;

namespace CodeSpace.Core.Services.Supervisor;

/// <summary>
/// Whether a re-plan has ALREADY been spent on a unit without moving its acceptance verdict — the fixed point the
/// decider's two re-plan steers used to feed. Pure over the tape (a replay re-derives the identical answer), off
/// the same two authorities the rest of the supervisor reads: <see cref="SupervisorPlanWindow.IsValidBoundary"/>
/// for what counts as a re-plan, and the positional <c>subtaskIds[i] ↔ agentResults[i]</c> join
/// <see cref="SupervisorDependencyGate.SubtaskIdsOf"/> publishes for which attempts belong to a unit.
///
/// <para>THE RULE, stated once: a unit's STANDING verdict (its latest attempt's) survived a re-plan when that same
/// verdict was ALREADY standing before the newest plan generation opened — either because nothing has re-graded it
/// since (the observed <c>plan→spawn→plan×6</c>: the rendered row is literally the same row, so its detail is
/// identical by construction), or because an attempt staged under the new plan came back with the identical
/// detail. Both readings say the same thing about the next action: another plan is a move the run has already made
/// against this unit, and it left this verdict where it found it.</para>
///
/// <para>Anchored on <see cref="SupervisorPlanWindow.IsValidBoundary"/> rather than on the decision KIND alone
/// (which is what <see cref="SupervisorAmendObligation"/> anchors its co-sign invalidation on, for its own MAJOR-8
/// reason): a plan that opened no generation — failed, empty, structurally invalid — re-planned nothing, so it must
/// not be able to withdraw the plan verb from a unit that has never actually had a second plan authored over it.
/// The two anchors cannot render contradictory prompt text, because the amend standings this reading yields to
/// (<see cref="SupervisorAmendStanding.AwaitingRetry"/> / <see cref="SupervisorAmendStanding.Consumed"/> /
/// <see cref="SupervisorAmendStanding.Discarded"/>) own their own steers and are read first.</para>
/// </summary>
public static class SupervisorReplanStanding
{
    /// <summary>Where this unit must be sent instead of at another plan, or <see cref="SupervisorReplanExit.None"/> when a re-plan is still an honest move. The admissibility half is the server's own amend gate, never a second reading of it, so the steer can only ever name a verb the turn's roster also offers.</summary>
    public static SupervisorReplanExit ExitFor(IReadOnlyList<SupervisorPriorDecision> priorDecisions, string? subtaskId)
    {
        if (!VerdictSurvivedAReplan(priorDecisions, subtaskId)) return SupervisorReplanExit.None;

        return SupervisorAmendPrecondition.IsAmendable(priorDecisions, subtaskId!) ? SupervisorReplanExit.ToAmendment : SupervisorReplanExit.ToHuman;
    }

    /// <summary>The TAPE half of <see cref="ExitFor"/>, with no policy in it: this unit's standing verdict is a graded failure that was already standing before the newest plan generation opened. False for a unit that was never graded, whose latest attempt passed, whose verification a human waived, or that no valid plan has followed.</summary>
    public static bool VerdictSurvivedAReplan(IReadOnlyList<SupervisorPriorDecision> priorDecisions, string? subtaskId)
    {
        if (subtaskId is null) return false;

        var planSequence = NewestGenerationSequence(priorDecisions);

        if (planSequence < 0) return false;

        var attempts = AttemptsOf(priorDecisions, subtaskId);

        if (attempts.Count == 0) return false;

        var standing = attempts[^1];

        if (!IsGradedFailure(standing.Result)) return false;

        return attempts.Any(a => a.Sequence < planSequence && IsGradedFailure(a.Result) && string.Equals(a.Result.AcceptanceDetail, standing.Result.AcceptanceDetail, StringComparison.Ordinal));
    }

    /// <summary>A verdict the server actually graded and REFUSED — never an ungraded pass-through, and never a human waiver (WAIVED ≠ FAILED at every door, the B2 invariant).</summary>
    private static bool IsGradedFailure(SupervisorAgentResult result) => result.AcceptancePassed == false && !SupervisorOutcome.IsWaived(result);

    /// <summary>The sequence of the newest decision that OPENS a plan generation, or -1 when nothing on the tape does.</summary>
    private static long NewestGenerationSequence(IReadOnlyList<SupervisorPriorDecision> priorDecisions)
    {
        var newest = -1L;

        foreach (var decision in priorDecisions)
            if (SupervisorPlanWindow.IsValidBoundary(decision)) newest = decision.Sequence;

        return newest;
    }

    /// <summary>EVERY folded attempt of this subtask across the whole tape, in tape order, with the sequence of the staging decision that produced it — the all-attempts sibling of <c>SupervisorDependencyGate.LatestResultsBySubtask</c>, which keeps only the last. The scope is the whole tape on purpose: the attempt a re-plan closed the plan window over is exactly the evidence this reading exists to compare against.</summary>
    private static IReadOnlyList<(long Sequence, SupervisorAgentResult Result)> AttemptsOf(IReadOnlyList<SupervisorPriorDecision> priorDecisions, string subtaskId)
    {
        var attempts = new List<(long Sequence, SupervisorAgentResult Result)>();

        foreach (var prior in priorDecisions.Where(d => SupervisorDecisionKinds.StagesAgents(d.DecisionKind)))
        {
            var ids = SupervisorDependencyGate.SubtaskIdsOf(prior);
            var results = SupervisorOutcome.ReadAgentResults(prior.OutcomeJson);

            for (var i = 0; i < ids.Count && i < results.Count; i++)
                if (string.Equals(ids[i], subtaskId, StringComparison.Ordinal)) attempts.Add((prior.Sequence, results[i]));
        }

        return attempts;
    }
}
