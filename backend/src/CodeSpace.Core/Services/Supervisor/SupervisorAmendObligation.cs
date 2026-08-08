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
    public static string? FirstOutstanding(SupervisorTurnContext context)
    {
        foreach (var (subtaskId, cardSequence) in ApprovedReplacementsAfterNewestPlan(context))
            if (LatestStagingSequence(context, subtaskId) < cardSequence)
                return subtaskId;

        return null;
    }

    /// <summary>Whether THIS subtask's latest attempt predates an approved amendment for it — its recorded verdict and contradiction were graded by the dead oracle, so retry escalation must not treat them as live evidence.</summary>
    public static bool IsOutstanding(SupervisorTurnContext context, string? subtaskId) =>
        subtaskId is not null
        && ApprovedReplacementsAfterNewestPlan(context).Any(a => a.SubtaskId == subtaskId && LatestStagingSequence(context, subtaskId) < a.CardSequence);

    /// <summary>Every approved REPLACEMENT amendment after the newest plan, in sequence order: (target subtask, the card's sequence).</summary>
    private static IEnumerable<(string SubtaskId, long CardSequence)> ApprovedReplacementsAfterNewestPlan(SupervisorTurnContext context)
    {
        var planSequence = -1L;

        foreach (var decision in context.PriorDecisions)
            if (decision.DecisionKind == SupervisorDecisionKinds.Plan) planSequence = decision.Sequence;

        foreach (var decision in context.PriorDecisions)
        {
            if (decision.Sequence <= planSequence) continue;

            if (!SupervisorAmendAcceptance.IsApprovedAmendCard(decision)) continue;

            var amend = SupervisorAmendAcceptance.ReadAmend(decision.PayloadJson)!;

            if (amend.Waive || string.IsNullOrWhiteSpace(amend.SubtaskId)) continue;

            yield return (amend.SubtaskId, decision.Sequence);
        }
    }

    /// <summary>The sequence of the LATEST staging decision (spawn/retry) that named this subtask, or -1 when it was never staged.</summary>
    private static long LatestStagingSequence(SupervisorTurnContext context, string subtaskId)
    {
        var latest = -1L;

        foreach (var decision in context.PriorDecisions)
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
