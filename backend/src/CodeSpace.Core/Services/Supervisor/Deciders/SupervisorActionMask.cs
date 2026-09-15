using System.Text;
using CodeSpace.Messages.Agents;

namespace CodeSpace.Core.Services.Supervisor.Deciders;

/// <summary>
/// A1.5 action mask: name the actions that are STRUCTURALLY unavailable this turn, so a futile verb is refused
/// before the model spends a turn on it rather than after. The schema's verb enum is deliberately NOT narrowed —
/// its eight-verb shape and order are a pinned commit contract, and a per-turn enum would make the wire contract
/// state-dependent; the mask is prompt-level guidance beside the run-bounds and budget recitations, and the turn's
/// roster (<see cref="SupervisorActionRoster"/>) renders it as its withheld half.
///
/// <para><b>It covers exactly the three verbs whose availability is a server-decided FACT</b> rather than a
/// judgement call — actions the durable tape proves cannot advance no matter how well the model argued for
/// them. <c>resolve</c>'s two unavailable states are materially different — one wastes a turn, the other ENDS THE
/// RUN:
/// <list type="bullet">
///   <item>No conflicted integration recorded ⇒ the executor no-ops the resolve with a skip reason
///         (<c>ResolveSkipReason</c>'s first arm) and the turn is spent for nothing.</item>
///   <item>A conflict exists but the resolve cap is spent ⇒ <c>SupervisorBounds.PostDecision</c> FORCE-STOPS the
///         whole run (<c>ResolveAttemptsExceeded</c>). Unlike an over-cap spawn wave, which is merely refused,
///         this one is the run's death — the strongest reason to state it before the choice, not after.</item>
/// </list>
/// <c>amend_acceptance</c> joined it once <see cref="SupervisorAmendPrecondition"/> (B4) made eligibility a
/// server verdict: with no unit whose latest check is an INFRA-classed failure, the proposal is rejected
/// synchronously — no card posted, no human spent, the turn gone — so it belongs beside <c>resolve</c> rather
/// than in the model's judgement. <c>merge</c> has TWO withheld states, both server-decided: after an executed clean
/// integration when no later staging decision actually produced an agent run (the current frontier is already folded,
/// so repeating the same fold cannot advance it), and when the newest staged work is a reconciliation the tape
/// records as NOT verified — the executor accepts a resolution only on
/// <see cref="SupervisorResolutionVerdict.Verified"/> (<c>SupervisorOutcome.ResolvedBranch</c>), so a merge there
/// re-runs the integrator over the branches that already conflicted rather than surfacing the resolver's branch.
/// That is the state golden <c>resolve-cap-spent</c> answered <c>merge</c> in on BOTH wires (run 34940616446, and
/// again on 2026-09-11 / 34027621996): the menu offered the verb, and nothing in the prompt said it could not
/// land.</para>
///
/// <para>Deliberately masks NOTHING else. <c>plan</c>, <c>ask_human</c> and <c>stop</c> are the escape hatches out
/// of every dead end and must always be offerable. <c>merge</c> is never masked from a guessed "nothing folded"
/// predicate: a later resolver/spawn/retry that really staged an agent reopens it, and a VERIFIED resolution keeps
/// the merge that accepts it. <c>spawn</c>/<c>retry</c> against an empty plan are not masked either — a
/// plan-less run keeps its goal-driven semantics, so their futility is a judgement, not a structural fact.</para>
/// </summary>
public static class SupervisorActionMask
{
    /// <summary>The block's pinned header — a stable prompt landmark, mirroring the bounds and budget recitations.</summary>
    public const string Header = "UNAVAILABLE THIS TURN (choosing one of these cannot advance the run):";

    /// <summary>The verbs this mask can withhold, in render order — the ONE table <see cref="Render"/> and <see cref="SupervisorActionRoster"/> both partition the vocabulary by, so a verb cannot be offered on the menu and forbidden underneath it.</summary>
    private static readonly string[] Maskable = [SupervisorDecisionKinds.Merge, SupervisorDecisionKinds.Resolve, SupervisorDecisionKinds.AmendAcceptance];

    /// <summary>Render the mask, or null when every action is available — the turn's roster carries this as its withheld half, so a verb named here is never on the menu above it.</summary>
    public static string? Render(SupervisorTurnContext context)
    {
        var withheld = Maskable.Select(verb => (Verb: verb, Reason: UnavailableReasonFor(verb, context))).Where(v => v.Reason is not null).ToList();

        if (withheld.Count == 0) return null;

        var builder = new StringBuilder(Header);

        foreach (var (verb, reason) in withheld) builder.Append('\n').Append("- ").Append(verb).Append(" — ").Append(reason);

        return builder.ToString();
    }

    /// <summary>Why <paramref name="verb"/> cannot advance the run this turn, else null (it is genuinely available). The SINGLE per-verb availability authority — the roster's menu and this block's withheld half both read it, so the two partition the vocabulary by construction instead of by agreement.</summary>
    internal static string? UnavailableReasonFor(string verb, SupervisorTurnContext context) => verb switch
    {
        SupervisorDecisionKinds.Merge => MergeUnavailableReason(context),
        SupervisorDecisionKinds.Resolve => ResolveUnavailableReason(context),
        SupervisorDecisionKinds.AmendAcceptance => AmendUnavailableReason(context),
        _ => null,
    };

    /// <summary>
    /// The non-null reason another merge cannot advance the durable frontier, else null. A reverse walk asks two
    /// narrow questions: has a REAL agent run been staged since the latest clean integration, and is the newest such
    /// staging one a merge could actually ACCEPT? Plan/ask/stop do not manufacture work. Spawn/retry/resolve reopen
    /// merge only when their recorded outcome says they actually staged at least one run, which keeps rejected/no-op
    /// staging from resurrecting an already-folded frontier; and a resolve reopens it only when its resolution is
    /// VERIFIED (<see cref="UnacceptedReconciliationReason"/>), which is the one resolution the executor accepts.
    /// </summary>
    internal static string? MergeUnavailableReason(SupervisorTurnContext context) => MergeUnavailableReason(context.PriorDecisions);

    /// <summary>The same answer over the RAW tape facts, for the callers that hold them without a <see cref="SupervisorTurnContext"/> — the plan recitation's finished-plan line reads it while it is still pure over the tape. Both entry points funnel here, exactly like <see cref="ResolveUnavailableReason(IReadOnlyList{SupervisorPriorDecision}, int?)"/>, so no caller can read a second opinion about whether this turn may merge.</summary>
    internal static string? MergeUnavailableReason(IReadOnlyList<SupervisorPriorDecision> priorDecisions)
    {
        foreach (var decision in priorDecisions.OrderByDescending(d => d.Sequence))
        {
            if (SupervisorDecisionKinds.StagesAgents(decision.DecisionKind)
                && decision.Status == SupervisorDecisionStatus.Succeeded
                && SupervisorOutcome.ReadStagedAgentCount(decision.OutcomeJson) > 0)
                return UnacceptedReconciliationReason(decision);

            if (SupervisorOutcome.MergeIntegratedABranch(decision))
                return "the latest integration is already clean and no later agent or resolver produced new work — another merge would fold the same frontier and cannot advance the run; stop if the goal is met";
        }

        return null;
    }

    /// <summary>The withheld-merge line once the newest staged work is a reconciliation the tape never accepted — a FACT and no third steer, for the reason the rest of this class states: the resolution verdict above it is already three-way and cap-aware, and the closing move below it is already reach-aware.</summary>
    internal const string UnacceptedReconciliation = "the newest work on this run is a reconciliation the tape records as NOT verified, and a merge cannot accept one — the integrator would re-run over the SAME branches that already conflicted and conflict again; the resolution verdict above names what is left";

    /// <summary>
    /// Why the newest agent-staging decision does NOT re-open <c>merge</c>: it is a <c>resolve</c> whose resolution
    /// the tape does not record as <see cref="SupervisorResolutionVerdict.Verified"/>. Reads the SAME verdict the
    /// executor's own acceptance rule reads (<c>SupervisorOutcome.ResolvedBranch</c> → <c>AcceptedResolutionBranch</c>),
    /// so the menu and the merge it would run can never disagree: without an ACCEPTED resolution that merge does not
    /// surface the resolver's branch, it re-runs the integrator over the original conflicting branches — the thing
    /// the executor's own comment says "would just re-conflict" — and buys a synthesis model call on the way.
    ///
    /// <para>Null for every other staging decision, so fresh spawn/retry work reopens merge exactly as before, and a
    /// VERIFIED resolution keeps the required merge this mask has always preserved.</para>
    /// </summary>
    private static string? UnacceptedReconciliationReason(SupervisorPriorDecision staging) =>
        staging.DecisionKind == SupervisorDecisionKinds.Resolve && SupervisorOutcome.ReadResolutionVerdict(staging.OutcomeJson) != SupervisorResolutionVerdict.Verified
            ? UnacceptedReconciliation
            : null;

    /// <summary>The non-null reason an amend proposal cannot advance the run this turn, else null. Reads <see cref="SupervisorAmendPrecondition"/> — the SAME gate the executor applies before any card is posted — so the mask and the refusal can never disagree about which units are amendable. What to do instead is NOT steered here: an outstanding co-sign already has its own banner, and a work-classed failure already has its own verdict line; a third steer authored here could only disagree with one of them.</summary>
    internal static string? AmendUnavailableReason(SupervisorTurnContext context) =>
        SupervisorAmendPrecondition.AnyAmendableUnit(context)
            ? null
            : "no unit has an infra-failed check to amend — the server rules on the RECORDED verdict, so a proposal here is refused before any human sees it and costs this turn";

    /// <summary>The non-null reason resolve cannot advance the run this turn, else null (it is genuinely available). Reads the SAME conflict-presence authority the resolve executor acts on, so the mask and the executor can never disagree about whether a conflict exists.</summary>
    internal static string? ResolveUnavailableReason(SupervisorTurnContext context) => ResolveUnavailableReason(context.PriorDecisions, context.MaxResolveAttempts);

    /// <summary>The same answer over the RAW tape facts, for the one caller that has them before a <see cref="SupervisorTurnContext"/> exists: the rehydrate step prerenders the stopped-now recital while it is still building the context. Both entry points funnel here, so no caller can read a second opinion.</summary>
    internal static string? ResolveUnavailableReason(IReadOnlyList<SupervisorPriorDecision> priorDecisions, int? maxResolveAttempts)
    {
        if (SupervisorOutcome.FindConflictDecision(priorDecisions) is null)
            return "no conflicted integration is recorded, so there is nothing to reconcile — a resolve would be a no-op and cost this turn";

        var (spent, cap) = ResolveBudget(priorDecisions, maxResolveAttempts);

        return spent >= cap
            ? $"the resolve cap is spent ({spent} of {cap}) — a further resolve does not get refused, it FORCE-STOPS this run. Stop and leave the conflict to a human, or ask one to rule"
            : null;
    }

    /// <summary>
    /// WHICH landing verb the stopped-now recital's steer may name, off the SAME two facts this mask masks
    /// <c>resolve</c> on. The refusal/advisory line and the mask sit in one prompt, so a steer derived anywhere else
    /// could name a verb the mask forbids three lines below it — which is precisely the shape of the live miss
    /// (run 34027621996): "Land that work" over a recorded conflict whose resolve cap was spent, leaving
    /// <c>merge</c> as the only reading and a blind re-merge of the same conflicted integration as the result.
    /// </summary>
    public static SupervisorLandingReach LandingReachFor(IReadOnlyList<SupervisorPriorDecision> priorDecisions, int? maxResolveAttempts)
    {
        if (SupervisorOutcome.FindConflictDecision(priorDecisions) is null) return SupervisorLandingReach.Unconstrained;

        return IsResolveCapSpent(priorDecisions, maxResolveAttempts) ? SupervisorLandingReach.NoLandingReachable : SupervisorLandingReach.ReconcileFirst;
    }

    /// <summary>
    /// Whether another resolve would FORCE-STOP the run rather than reconcile. The ONE answer both this mask and
    /// the resolution-verdict copy read: the two sit in the same prompt, so a disagreement would tell the model to
    /// issue a resolve and forbid it in the same breath — which is exactly what shipped before this was shared.
    /// </summary>
    public static bool IsResolveCapSpent(SupervisorTurnContext context) => IsResolveCapSpent(context.PriorDecisions, context.MaxResolveAttempts);

    /// <summary>The same budget read over the raw tape facts — see the <see cref="ResolveUnavailableReason(IReadOnlyList{SupervisorPriorDecision}, int?)"/> overload for why a context-less entry point exists.</summary>
    public static bool IsResolveCapSpent(IReadOnlyList<SupervisorPriorDecision> priorDecisions, int? maxResolveAttempts)
    {
        var (spent, cap) = ResolveBudget(priorDecisions, maxResolveAttempts);

        return spent >= cap;
    }

    /// <summary>Resolves spent-vs-cap the way <c>SupervisorBounds.PostDecision</c> counts it — off the tape, with the lane default standing in for a context that carries no cap.</summary>
    private static (int Spent, int Cap) ResolveBudget(IReadOnlyList<SupervisorPriorDecision> priorDecisions, int? maxResolveAttempts) =>
        (priorDecisions.Count(d => d.DecisionKind == SupervisorDecisionKinds.Resolve),
         maxResolveAttempts ?? SupervisorLane.DefaultMaxResolveAttempts);
}
