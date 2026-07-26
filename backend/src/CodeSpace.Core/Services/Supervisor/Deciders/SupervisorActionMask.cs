using System.Text;
using CodeSpace.Messages.Agents;

namespace CodeSpace.Core.Services.Supervisor.Deciders;

/// <summary>
/// A1.5 action mask, v1: name the actions that are STRUCTURALLY unavailable this turn, so a futile verb is refused
/// before the model spends a turn on it rather than after. The schema's verb enum is deliberately NOT narrowed —
/// its seven-verb shape and order are a pinned commit contract, and a per-turn enum would make the wire contract
/// state-dependent; the mask is prompt-level guidance beside the run-bounds and budget recitations.
///
/// <para><b>v1 covers exactly the <c>resolve</c> verb</b>, because it is the only verb whose availability is a
/// server-decided FACT rather than a judgement call, and because it is the verb most newly at risk: the rails now
/// name it (a model that never knew resolve existed could not misfire it). Its two unavailable states are
/// materially different — one wastes a turn, the other ENDS THE RUN:
/// <list type="bullet">
///   <item>No conflicted integration recorded ⇒ the executor no-ops the resolve with a skip reason
///         (<c>ResolveSkipReason</c>'s first arm) and the turn is spent for nothing.</item>
///   <item>A conflict exists but the resolve cap is spent ⇒ <c>SupervisorBounds.PostDecision</c> FORCE-STOPS the
///         whole run (<c>ResolveAttemptsExceeded</c>). Unlike an over-cap spawn wave, which is merely refused,
///         this one is the run's death — the strongest reason to state it before the choice, not after.</item>
/// </list></para>
///
/// <para>Deliberately masks NOTHING else. <c>plan</c>, <c>ask_human</c> and <c>stop</c> are the escape hatches out
/// of every dead end and must always be offerable. <c>merge</c> is never masked on "nothing folded": the merge set
/// excludes a resolve's own agent run, so that predicate reads futile in exactly the state where merging a
/// VERIFIED resolution is correct. <c>spawn</c>/<c>retry</c> against an empty plan are not masked either — a
/// plan-less run keeps its goal-driven semantics, so their futility is a judgement, not a structural fact.</para>
/// </summary>
public static class SupervisorActionMask
{
    /// <summary>The block's pinned header — a stable prompt landmark, mirroring the bounds and budget recitations.</summary>
    public const string Header = "UNAVAILABLE THIS TURN (choosing one of these cannot advance the run):";

    /// <summary>Render the mask, or null when every action is available — a healthy run's prompt stays byte-identical, which the auto-compaction and token-budget characteristics depend on.</summary>
    public static string? Render(SupervisorTurnContext context)
    {
        if (ResolveUnavailableReason(context) is not { } reason) return null;

        return $"{Header}\n- resolve — {reason}";
    }

    /// <summary>The non-null reason resolve cannot advance the run this turn, else null (it is genuinely available). Reads the SAME conflict-presence authority the resolve executor acts on, so the mask and the executor can never disagree about whether a conflict exists.</summary>
    internal static string? ResolveUnavailableReason(SupervisorTurnContext context)
    {
        if (SupervisorOutcome.FindConflictDecision(context.PriorDecisions) is null)
            return "no conflicted integration is recorded, so there is nothing to reconcile — a resolve would be a no-op and cost this turn";

        var (spent, cap) = ResolveBudget(context);

        return spent >= cap
            ? $"the resolve cap is spent ({spent} of {cap}) — a further resolve does not get refused, it FORCE-STOPS this run. Stop and leave the conflict to a human, or ask one to rule"
            : null;
    }

    /// <summary>
    /// Whether another resolve would FORCE-STOP the run rather than reconcile. The ONE answer both this mask and
    /// the resolution-verdict copy read: the two sit in the same prompt, so a disagreement would tell the model to
    /// issue a resolve and forbid it in the same breath — which is exactly what shipped before this was shared.
    /// </summary>
    public static bool IsResolveCapSpent(SupervisorTurnContext context)
    {
        var (spent, cap) = ResolveBudget(context);

        return spent >= cap;
    }

    /// <summary>Resolves spent-vs-cap the way <c>SupervisorBounds.PostDecision</c> counts it — off the tape, with the lane default standing in for a context that carries no cap.</summary>
    private static (int Spent, int Cap) ResolveBudget(SupervisorTurnContext context) =>
        (context.PriorDecisions.Count(d => d.DecisionKind == SupervisorDecisionKinds.Resolve),
         context.MaxResolveAttempts ?? SupervisorLane.DefaultMaxResolveAttempts);
}
