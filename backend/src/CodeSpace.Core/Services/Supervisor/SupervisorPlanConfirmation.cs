using System.Text.Json;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Messages.Agents;

namespace CodeSpace.Core.Services.Supervisor;

/// <summary>
/// The plan-confirmation gate (triad slice S3) — when the operator opted in (<c>requirePlanConfirmation</c>),
/// the supervisor's freshly-AUTHORED plan must be confirmed by a human before ANY agent runs. The turn loop
/// injects an <c>ask_human</c> confirmation card right after an unconfirmed plan (REUSING E4's durable HITL
/// park — same card, same Action wait, same resume path as <see cref="SupervisorApprovalRequest"/>), so the
/// run parks Suspended instead of spawning. The human's answer gates the next turn: an approving reply
/// releases execution (the WorkPlan flips to Confirmed and the decider proceeds to spawn); ANY other reply is
/// revision feedback the decider folds into a REVISED plan version (the old version flips to Rejected, the
/// new version re-gates) — the deer-flow <c>[ACCEPTED]/[EDIT_PLAN]</c> interrupt, on the durable tape.
///
/// <para>Detection is PURE over the prior-decision tape (find the latest terminal <c>plan</c>, look for this
/// gate's own card after it), so a crash-replay re-derives the identical injection and the per-turn
/// idempotency key stays stable — the same determinism contract as the approval gate. The card is recognised
/// by the <see cref="ConfirmationMarker"/> phrase in its question, DISTINCT from
/// <see cref="SupervisorApprovalRequest.ApprovalMarker"/> so the two gates never claim each other's cards.</para>
/// </summary>
public static class SupervisorPlanConfirmation
{
    /// <summary>The marker phrase EVERY plan-confirmation question carries — the stable, load-bearing tail the gate matches to recognise its OWN card (vs a content ask_human or an approval card). Pinned by a unit test so a reword is a visible decision.</summary>
    public const string ConfirmationMarker = "Reply 'approve' to run this plan, or describe the changes you want.";

    /// <summary>Build the confirmation ask_human for the run's current plan version. Deterministic given (version, itemCount, delivery, priorApprovedDelivery) — a replayed turn re-derives the same card bytes → the same idempotency key.</summary>
    public static SupervisorDecision IntoAskHuman(int planVersion, int itemCount, DeliverySpec? delivery, DeliverySpec? priorApprovedDelivery) => new()
    {
        Kind = SupervisorDecisionKinds.AskHuman,
        PayloadJson = JsonSerializer.Serialize(new SupervisorAskHumanPayload { Question = QuestionFor(planVersion, itemCount, delivery, priorApprovedDelivery) }, AgentJson.Options),
    };

    /// <summary>
    /// The delivery contract of the MOST RECENTLY APPROVED plan version — the baseline a fresh confirmation card
    /// compares the CURRENT plan's contract against, so a re-plan that WEAKENS an already-approved automatic pull
    /// request is flagged as a downgrade instead of silently vanishing. A forward scan: each Plan decision's own
    /// (already server-clamped, per <see cref="Supervisor.SupervisorDeliveryClamp"/>) delivery becomes the
    /// candidate baseline; it commits ONLY when the very next confirmation card for it is answered with approval
    /// — a REJECTED plan's contract never counts as "was in effect," and the tape's own latest plan (which has no
    /// card yet at gate time) is naturally excluded. Never re-reads <see cref="SupervisorTurnContext.DeliverySpec"/>:
    /// once the operator declares a field, <see cref="Supervisor.SupervisorDeliveryClamp"/> makes that field
    /// permanently sticky in every future plan's own clamp, so an operator-declared value can never register as a
    /// downgrade — comparing the approved baseline alone is sufficient.
    /// </summary>
    public static DeliverySpec? LastApprovedDelivery(IReadOnlyList<SupervisorPriorDecision> priors)
    {
        DeliverySpec? candidate = null;
        DeliverySpec? approved = null;

        foreach (var decision in priors)
        {
            if (decision.DecisionKind == SupervisorDecisionKinds.Plan)
            {
                candidate = SupervisorOutcome.ReadPlanDelivery(decision.PayloadJson);
                continue;
            }

            if (!IsConfirmationCard(decision)) continue;

            var answer = SupervisorOutcome.ReadAskHumanAnswer(decision.OutcomeJson);

            if (answer != null && Approves(answer)) approved = candidate;
        }

        return approved;
    }

    /// <summary>
    /// The tape's LATEST plan decision, or null when none exists — DC-1's own read for anything that needs the
    /// CURRENT plan's own payload (its delivery contract), not just this gate's confirmation bookkeeping.
    /// </summary>
    public static SupervisorPriorDecision? LatestPlanDecision(IReadOnlyList<SupervisorPriorDecision> priors)
    {
        var index = LastPlanIndex(priors);

        return index < 0 ? null : priors[index];
    }

    /// <summary>
    /// Whether the tape's LATEST terminal plan still awaits confirmation — true when a plan decision exists and
    /// no SURFACED confirmation card follows it. A re-plan (a newer plan after an answered card) re-gates
    /// naturally: the scan anchors on the newest plan, and the old card sits BEFORE it. Only a card that actually
    /// REACHED a human counts — one with a recorded wait token (parked) or a folded answer; a degraded no-surface
    /// card satisfies nothing, so a run that cannot post the question can never silently bypass the gate (the
    /// turn loop stops it with <c>PlanConfirmationUnavailable</c> instead). An unanswered surfaced card in the
    /// priors only occurs mid-crash-recovery, where the pending decision row itself re-parks.
    /// </summary>
    public static bool NeedsConfirmation(SupervisorTurnContext context)
    {
        var lastPlan = LastPlanIndex(context.PriorDecisions);

        if (lastPlan < 0) return false;

        for (var i = lastPlan + 1; i < context.PriorDecisions.Count; i++)
            if (IsConfirmationCard(context.PriorDecisions[i]) && CardWasSurfaced(context.PriorDecisions[i])) return false;

        return true;
    }

    /// <summary>
    /// Whether the tape's LATEST plan stands REJECTED — its confirmation card was answered with revision
    /// feedback and NO newer plan has been authored since. The turn loop refuses any spawn/retry while this
    /// holds (a rejected plan may never be executed); authoring a revised version clears it by construction.
    /// </summary>
    public static bool LatestPlanRejected(SupervisorTurnContext context)
    {
        var lastPlan = LastPlanIndex(context.PriorDecisions);

        if (lastPlan < 0) return false;

        for (var i = context.PriorDecisions.Count - 1; i > lastPlan; i--)
        {
            var decision = context.PriorDecisions[i];

            if (!IsConfirmationCard(decision)) continue;

            var answer = SupervisorOutcome.ReadAskHumanAnswer(decision.OutcomeJson);

            return answer != null && !Approves(answer);
        }

        return false;
    }

    /// <summary>A confirmation card is ANSWERED when a human's reply is folded into its outcome — human engagement, which the no-progress guard counts as progress (an operator actively revising a plan is not a stalled run).</summary>
    public static bool IsAnsweredConfirmationCard(SupervisorPriorDecision decision) =>
        IsConfirmationCard(decision) && SupervisorOutcome.ReadAskHumanAnswer(decision.OutcomeJson) != null;

    /// <summary>A card SURFACED iff it parked on a wait (token recorded) or already carries an answer — a degraded no-surface card did neither and must not satisfy the gate.</summary>
    private static bool CardWasSurfaced(SupervisorPriorDecision decision) =>
        SupervisorOutcome.ReadHumanWaitToken(decision.OutcomeJson) != null || SupervisorOutcome.ReadAskHumanAnswer(decision.OutcomeJson) != null;

    /// <summary>
    /// Whether the immediately-preceding decision is an ANSWERED confirmation card — the once-only release edge
    /// the turn loop flips the WorkPlan status on (approve → Confirmed, anything else → Rejected + the decider
    /// authors a revision). Position-bound to <c>[^1]</c> like <see cref="SupervisorApprovalRequest.WasJustApproved"/>:
    /// on later turns the last decision is the released spawn / revised plan, so the flip never re-fires.
    /// </summary>
    public static bool WasJustAnswered(SupervisorTurnContext context, out bool approved)
    {
        approved = false;

        var last = context.PriorDecisions.Count > 0 ? context.PriorDecisions[^1] : null;

        if (last == null || !IsConfirmationCard(last)) return false;

        var answer = SupervisorOutcome.ReadAskHumanAnswer(last.OutcomeJson);

        if (answer == null) return false;

        approved = Approves(answer);

        return true;
    }

    /// <summary>A decision is this gate's confirmation card iff it is an ask_human whose question carries the confirmation marker — a content ask_human or an approval card never does.</summary>
    public static bool IsConfirmationCard(SupervisorPriorDecision decision) =>
        decision.DecisionKind == SupervisorDecisionKinds.AskHuman && QuestionCarriesMarker(decision.PayloadJson);

    /// <summary>Whether an ask_human payload's question carries the confirmation marker — the payload-level match the confirm endpoint reuses to locate the pending card.</summary>
    public static bool QuestionCarriesMarker(string? askHumanPayloadJson)
    {
        if (string.IsNullOrEmpty(askHumanPayloadJson)) return false;

        try
        {
            var root = JsonDocument.Parse(askHumanPayloadJson).RootElement;

            return root.ValueKind == JsonValueKind.Object && root.TryGetProperty("question", out var q)
                && q.ValueKind == JsonValueKind.String && (q.GetString() ?? "").Contains(ConfirmationMarker, StringComparison.Ordinal);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>The human's answer approves iff it begins with the approve reply word (case-insensitive, trimmed) — the same fail-closed predicate as the approval gate (<see cref="SupervisorApprovalRequest.ApproveReply"/>). Anything else is revision feedback.</summary>
    private static bool Approves(string answer) =>
        answer.TrimStart().StartsWith(SupervisorApprovalRequest.ApproveReply, StringComparison.OrdinalIgnoreCase);

    private static int LastPlanIndex(IReadOnlyList<SupervisorPriorDecision> priors)
    {
        for (var i = priors.Count - 1; i >= 0; i--)
            if (priors[i].DecisionKind == SupervisorDecisionKinds.Plan) return i;

        return -1;
    }

    /// <summary>The confirmation question naming the plan version + size + (DC-1/DC-2a) the effective delivery contract, so the operator knows EVERYTHING they are approving before opening the checklist — including a pull request the run would open automatically on completion, that it explicitly will NOT, or that this plan revokes an already-approved one.</summary>
    private static string QuestionFor(int planVersion, int itemCount, DeliverySpec? delivery, DeliverySpec? priorApprovedDelivery) =>
        $"The supervisor authored plan v{planVersion} with {itemCount} step(s).{DeliverySummary(delivery, priorApprovedDelivery)} Review the plan checklist, then confirm. {ConfirmationMarker}";

    /// <summary>
    /// DC-1/DC-2a — a leading-space sentence naming the effective delivery contract this plan carries, so the
    /// card never promises silence on a behavior the actual stop will diverge from. Three cases, in priority
    /// order: (1) the plan itself opens a PR — name the target branch; (2) the plan does NOT open one but an
    /// EARLIER plan version's PR was already approved — flag the REVOCATION explicitly, since silently dropping
    /// it would let the operator approve a checklist believing the prior promise still stands; (3) the plan
    /// explicitly declines with no prior approval to revoke — state the decline (requirement b: an explicit
    /// <c>false</c> must render something, not nothing). No contract at all on either side ⇒ empty (byte-identical
    /// to pre-DC-1).
    /// </summary>
    private static string DeliverySummary(DeliverySpec? delivery, DeliverySpec? priorApprovedDelivery)
    {
        if (delivery?.OpenPullRequest == true)
        {
            var branch = string.IsNullOrWhiteSpace(delivery.TargetBranch) ? "the repository's default branch" : delivery.TargetBranch;

            return $" On completion it will automatically open a pull request against {branch}.";
        }

        if (priorApprovedDelivery?.OpenPullRequest == true)
            return " This REVOKES the automatic pull request an earlier plan version already had approved — it will NOT open on completion unless a future plan restores it.";

        if (delivery?.OpenPullRequest == false)
            return " It will NOT automatically open a pull request on completion.";

        return "";
    }
}
