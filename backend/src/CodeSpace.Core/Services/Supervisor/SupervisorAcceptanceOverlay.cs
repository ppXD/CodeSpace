using CodeSpace.Core.Services.Agents;
using CodeSpace.Messages.Agents;

namespace CodeSpace.Core.Services.Supervisor;

/// <summary>
/// The co-sign overlay (amend-acceptance arc, B3) — the ONE chokepoint that turns APPROVED amend cards into the
/// run's EFFECTIVE oracle view. Both readers of "which spec grades which subtask" resolve through it — the fold's
/// per-unit grade (<c>FoldUnitAcceptanceGradeAsync</c>) and the spawn path's planned-subtask lookup
/// (<c>ResolvePlannedSubtasks</c>, whose <c>Acceptance</c> drives the F4 forced-push opt-in) — so an amendment can
/// never be honored at one read and missed at the other (an amend ADDING a spec would otherwise run its retry with
/// push OFF and fail "no-branch-or-repo").
///
/// <para>Authority is PAIRWISE (the FATAL-2 rule): an amendment applies only off a card whose OWN resolved
/// Action-wait answer approves — never a tape-wide "any approval" walk — and the applied proposal is read back
/// from THAT approved card's immutable payload (<see cref="SupervisorAmendAcceptance.ReadAmend"/>), so approving
/// card A can never authorize different bytes. Anchoring is NEWEST-PLAN (the MAJOR-8 rule, the
/// <c>NeedsConfirmation</c> precedent, NOT the <c>LastApprovedDelivery</c> one): a re-plan resets the base map and
/// INVALIDATES every earlier amendment — a plan-v2 waiver must never silently attach to a re-used id in v3. Cards
/// apply in sequence order, so the LATEST approved amendment per subtask wins (deterministic; a replay re-derives
/// the identical view). An approved amendment whose replacement spec fails
/// <see cref="AgentAcceptanceContract.ValidateAuthored"/> is fail-CLOSED to the ORIGINAL oracle (the MAJOR-4 rule
/// — an invalid approved spec never silently drops a unit to ungraded).</para>
/// </summary>
public static class SupervisorAcceptanceOverlay
{
    /// <summary>The run's effective oracle view: the per-subtask specs after approved amendments, and the subtasks whose verification a human WAIVED (no spec — the fold stamps them <c>Waived</c> instead of grading; WAIVED ≠ PASSED at every door, the B2 invariant).</summary>
    public sealed record EffectiveAcceptance(IReadOnlyDictionary<string, SupervisorAcceptanceSpec> BySubtask, IReadOnlySet<string> WaivedSubtaskIds);

    /// <summary>Apply every approved amend card recorded AFTER the newest plan onto <paramref name="plannedBySubtask"/> (the newest plan's own authored map). Pure over the tape — same decisions, same view.</summary>
    public static EffectiveAcceptance Resolve(IReadOnlyList<SupervisorPriorDecision> priorDecisions, IReadOnlyDictionary<string, SupervisorAcceptanceSpec> plannedBySubtask)
    {
        var specs = new Dictionary<string, SupervisorAcceptanceSpec>(plannedBySubtask);
        var waived = new HashSet<string>(StringComparer.Ordinal);

        foreach (var decision in DecisionsAfterNewestPlan(priorDecisions))
        {
            if (!SupervisorAmendAcceptance.IsAmendCard(decision)) continue;

            if (!Approves(SupervisorOutcome.ReadAskHumanAnswer(decision.OutcomeJson))) continue;

            var amend = SupervisorAmendAcceptance.ReadAmend(decision.PayloadJson)!;

            if (string.IsNullOrWhiteSpace(amend.SubtaskId)) continue;

            if (amend.Waive)
            {
                specs.Remove(amend.SubtaskId);
                waived.Add(amend.SubtaskId);
            }
            else if (amend.Acceptance is { } replacement && AgentAcceptanceContract.ValidateAuthored(replacement) is null)
            {
                specs[amend.SubtaskId] = replacement;
                waived.Remove(amend.SubtaskId);
            }
        }

        return new EffectiveAcceptance(specs, waived);
    }

    /// <summary>The decided decisions strictly AFTER the newest plan — an amendment authorized against a superseded plan never survives a re-plan.</summary>
    private static IEnumerable<SupervisorPriorDecision> DecisionsAfterNewestPlan(IReadOnlyList<SupervisorPriorDecision> priorDecisions)
    {
        var planIndex = -1;

        for (var i = priorDecisions.Count - 1; i >= 0; i--)
            if (priorDecisions[i].DecisionKind == SupervisorDecisionKinds.Plan) { planIndex = i; break; }

        for (var i = planIndex + 1; i < priorDecisions.Count; i++)
            yield return priorDecisions[i];
    }

    /// <summary>The card's OWN answer approves — the same reply word every marker card family member reads; a null (unanswered / degraded) or redirecting answer applies nothing.</summary>
    private static bool Approves(string? answer) =>
        answer is not null && answer.TrimStart().StartsWith(SupervisorApprovalRequest.ApproveReply, StringComparison.OrdinalIgnoreCase);
}
