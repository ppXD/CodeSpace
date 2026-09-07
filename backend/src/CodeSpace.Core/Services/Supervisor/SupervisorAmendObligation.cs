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
        var planSequence = NewestPlanSequence(priorDecisions);

        foreach (var (subtaskId, cardSequence) in ApprovedReplacements(priorDecisions))
            if (cardSequence > planSequence && LatestStagingSequence(priorDecisions, subtaskId) < cardSequence)
                return subtaskId;

        return null;
    }

    /// <summary>Whether ANY approved REPLACEMENT amendment sits on this tape at all — anchored to the current plan or already discarded by a later one. The single fact the prompt's once-per-turn re-plan-cost note renders from, so a run that has spent a human's co-sign is told what a <c>plan</c> costs exactly once, however many amended units its results block carries.</summary>
    public static bool AnyApprovedAmendment(IReadOnlyList<SupervisorPriorDecision> priorDecisions) => ApprovedReplacements(priorDecisions).Any();

    /// <summary>Whether THIS subtask's latest attempt predates an approved amendment for it — its recorded verdict and contradiction were graded by the dead oracle, so retry escalation must not treat them as live evidence.</summary>
    public static bool IsOutstanding(SupervisorTurnContext context, string? subtaskId) => IsOutstanding(context.PriorDecisions, subtaskId);

    /// <summary>The priors-only overload of <see cref="IsOutstanding(SupervisorTurnContext, string?)"/> — the AwaitingRetry reading of <see cref="StandingFor"/>, never a second walk that could answer differently.</summary>
    public static bool IsOutstanding(IReadOnlyList<SupervisorPriorDecision> priorDecisions, string? subtaskId) =>
        StandingFor(priorDecisions, subtaskId) == SupervisorAmendStanding.AwaitingRetry;

    /// <summary>
    /// Where this subtask stands against the co-signed amendments on the tape — the fuller reading
    /// <see cref="IsOutstanding"/> collapses to one bit. The prompt needs the other states as well: a unit whose
    /// amendment was already CONSUMED and whose check still cannot run is not owed a retry, but re-planning it is
    /// still the one move that destroys the human's ruling; and a unit whose amendment a re-plan ALREADY discarded
    /// (<see cref="SupervisorAmendStanding.Discarded"/>) must be sent back to <c>amend_acceptance</c>, never told to
    /// author one more plan — the exact loop that spent run 34066916864.
    ///
    /// <para>Cards apply in sequence order, so the LATEST approved amendment per subtask decides — the co-sign
    /// overlay's own rule. A card that predates the newest plan reads Discarded (MAJOR-8: the plan it was anchored
    /// to is gone), and because sequences are monotonic that is exactly "no card was co-signed since the re-plan".
    /// Pure over the tape: a replay re-derives the identical answer.</para>
    ///
    /// <para>KNOWN HOLE, deliberately left for its own change: <see cref="LatestStagingSequence"/> counts a
    /// spawn/retry decision by KIND alone, ignoring its <c>Status</c> — so a retry row that never actually staged an
    /// agent (Failed / Expired) still reads as consuming the co-sign, flipping AwaitingRetry to Consumed and
    /// silencing the banner on an obligation nothing discharged. Reading Status here would move
    /// <see cref="IsOutstanding"/> for all of its callers (the amend precondition, the retry-escalation gate, the
    /// banner), so it belongs in a change that can be graded on that blast radius, not in the steer's.</para>
    /// </summary>
    public static SupervisorAmendStanding StandingFor(IReadOnlyList<SupervisorPriorDecision> priorDecisions, string? subtaskId)
    {
        if (subtaskId is null) return SupervisorAmendStanding.None;

        var cardSequence = -1L;

        foreach (var (id, sequence) in ApprovedReplacements(priorDecisions))
            if (string.Equals(id, subtaskId, StringComparison.Ordinal)) cardSequence = sequence;

        if (cardSequence < 0) return SupervisorAmendStanding.None;

        if (cardSequence <= NewestPlanSequence(priorDecisions)) return SupervisorAmendStanding.Discarded;

        return LatestStagingSequence(priorDecisions, subtaskId) < cardSequence ? SupervisorAmendStanding.AwaitingRetry : SupervisorAmendStanding.Consumed;
    }

    /// <summary>The sequence of the NEWEST plan on the tape, or -1 when nothing has been planned — the anchor every approved amendment lives or dies by (MAJOR-8).</summary>
    private static long NewestPlanSequence(IReadOnlyList<SupervisorPriorDecision> priorDecisions)
    {
        var planSequence = -1L;

        foreach (var decision in priorDecisions)
            if (decision.DecisionKind == SupervisorDecisionKinds.Plan) planSequence = decision.Sequence;

        return planSequence;
    }

    /// <summary>Every approved REPLACEMENT amendment on the tape, in sequence order: (target subtask, the card's sequence). Unfiltered by plan — the callers that care about the anchor compare against <see cref="NewestPlanSequence"/> themselves, because "discarded by a re-plan" is a reading the prompt needs, not a row to hide.</summary>
    private static IEnumerable<(string SubtaskId, long CardSequence)> ApprovedReplacements(IReadOnlyList<SupervisorPriorDecision> priorDecisions)
    {
        foreach (var decision in priorDecisions)
        {
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
