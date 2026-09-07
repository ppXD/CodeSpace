using CodeSpace.Messages.Agents;

namespace CodeSpace.Core.Services.Supervisor;

/// <summary>
/// The retry-after-amend obligation (amend-acceptance arc, B5 — the MAJOR-5 rung): an APPROVED spec amendment is a
/// commitment to RE-GRADE its target under the co-signed oracle — the fold's already-graded guard means the
/// amendment only ever affects a FUTURE attempt, so until one is staged the target's standing verdict was graded
/// by the DEAD oracle. A run that stops on that stale verdict never actually consumed what the human signed.
///
/// <para>An obligation is OUTSTANDING when an approved amend card (newest-plan-anchored, the same
/// <see cref="SupervisorAmendAcceptance.IsApprovedAmendCard"/> authority the overlay applies) proposes a
/// REPLACEMENT spec (a waive owes nothing — the unit is settled as Waived at the next fold, no retry involved)
/// and NO staging decision for its target subtask was recorded AFTER the card. Staging consumes it by
/// construction: the new attempt folds its grade under the overlay's effective oracle. A re-plan invalidates the
/// amendment (MAJOR-8) and with it the obligation. Pure over the tape — a replay re-derives the identical answer.</para>
/// </summary>
public static class SupervisorAmendObligation
{
    /// <summary>The first outstanding obligation's target subtask id (sequence order — deterministic), or null when every approved amendment has been consumed.</summary>
    public static string? FirstOutstanding(SupervisorTurnContext context) => FirstOutstanding(context.PriorDecisions);

    /// <summary>The priors-only overload — the recitation and other pure renderers resolve the SAME walk without a context.</summary>
    public static string? FirstOutstanding(IReadOnlyList<SupervisorPriorDecision> priorDecisions)
    {
        foreach (var (subtaskId, cardSequence) in ApprovedReplacementsAfterNewestPlan(priorDecisions))
            if (LatestStagingSequence(priorDecisions, subtaskId) < cardSequence)
                return subtaskId;

        return null;
    }

    /// <summary>Whether THIS subtask's latest attempt predates an approved amendment for it — its recorded verdict and contradiction were graded by the dead oracle, so retry escalation must not treat them as live evidence.</summary>
    public static bool IsOutstanding(SupervisorTurnContext context, string? subtaskId) => IsOutstanding(context.PriorDecisions, subtaskId);

    /// <summary>The priors-only overload of <see cref="IsOutstanding(SupervisorTurnContext, string?)"/> — the AwaitingRetry reading of <see cref="StandingFor"/>, never a second walk that could answer differently.</summary>
    public static bool IsOutstanding(IReadOnlyList<SupervisorPriorDecision> priorDecisions, string? subtaskId) =>
        StandingFor(priorDecisions, subtaskId) == SupervisorAmendStanding.AwaitingRetry;

    /// <summary>
    /// Where this subtask stands against the co-signed amendments on the tape — the fuller reading
    /// <see cref="IsOutstanding"/> collapses to one bit. The prompt needs the third state as well: a unit whose
    /// amendment was already CONSUMED and whose check still cannot run is not owed a retry, but re-planning it is
    /// still the one move that destroys the human's ruling, and the steer has to say so.
    ///
    /// <para>Cards apply in sequence order, so the LATEST approved amendment per subtask decides — the co-sign
    /// overlay's own rule. Pure over the tape: a replay re-derives the identical answer.</para>
    /// </summary>
    public static SupervisorAmendStanding StandingFor(IReadOnlyList<SupervisorPriorDecision> priorDecisions, string? subtaskId)
    {
        if (subtaskId is null) return SupervisorAmendStanding.None;

        var standing = SupervisorAmendStanding.None;

        foreach (var (id, cardSequence) in ApprovedReplacementsAfterNewestPlan(priorDecisions))
        {
            if (!string.Equals(id, subtaskId, StringComparison.Ordinal)) continue;

            standing = LatestStagingSequence(priorDecisions, subtaskId) < cardSequence ? SupervisorAmendStanding.AwaitingRetry : SupervisorAmendStanding.Consumed;
        }

        return standing;
    }

    /// <summary>Every approved REPLACEMENT amendment after the newest plan, in sequence order: (target subtask, the card's sequence).</summary>
    private static IEnumerable<(string SubtaskId, long CardSequence)> ApprovedReplacementsAfterNewestPlan(IReadOnlyList<SupervisorPriorDecision> priorDecisions)
    {
        var planSequence = -1L;

        foreach (var decision in priorDecisions)
            if (decision.DecisionKind == SupervisorDecisionKinds.Plan) planSequence = decision.Sequence;

        foreach (var decision in priorDecisions)
        {
            if (decision.Sequence <= planSequence) continue;

            if (!SupervisorAmendAcceptance.IsApprovedAmendCard(decision)) continue;

            var amend = SupervisorAmendAcceptance.ReadAmend(decision.PayloadJson)!;

            if (amend.Waive || string.IsNullOrWhiteSpace(amend.SubtaskId)) continue;

            yield return (amend.SubtaskId, decision.Sequence);
        }
    }

    /// <summary>The sequence of the LATEST staging decision (spawn/retry) that named this subtask, or -1 when it was never staged.</summary>
    private static long LatestStagingSequence(IReadOnlyList<SupervisorPriorDecision> priorDecisions, string subtaskId)
    {
        var latest = -1L;

        foreach (var decision in priorDecisions)
        {
            if (!SupervisorDecisionKinds.StagesAgents(decision.DecisionKind)) continue;

            var named = decision.DecisionKind == SupervisorDecisionKinds.Spawn
                ? SupervisorOutcome.ReadSpawnSubtaskIds(decision.PayloadJson).Contains(subtaskId)
                : SupervisorOutcome.ReadRetrySubtaskId(decision.PayloadJson) == subtaskId;

            if (named && decision.Sequence > latest) latest = decision.Sequence;
        }

        return latest;
    }
}
