using CodeSpace.Messages.Decisions;

namespace CodeSpace.Core.Services.Decisions;

/// <summary>
/// The fail-closed policy FLOOR for the Decision substrate (D4) — the single server-side enforcement point that decides
/// WHO may answer a decision, RAISE-ONLY: it can force a decision up to <see cref="DecisionPolicies.HumanRequired"/>,
/// but never down. A raiser (an <c>agent.run</c> mid-run, a <c>flow.decision</c> node) DECLARES a policy
/// (<c>auto_allowed</c> / <c>supervisor_first</c> / <c>human_required</c>); this clamps it so a relabeled or
/// over-permissive declaration can never let a high-stakes decision auto-resolve without a person.
///
/// <para>Applied at BOTH envelope build sites (<c>FlowDecisionNode.BuildRequest</c>, <c>McpRequestHandler.BuildDecisionRequest</c>)
/// so the STASHED envelope already carries the effective policy — the D3 queue and the future D4 supervisor arbiter both
/// read it for free, and the arbiter additionally re-checks it (defense-in-depth against a tampered stored envelope).
/// Mirrors <see cref="Supervisor.SupervisorGovernance"/>: a pure, fail-closed, unit-pinned clamp.</para>
///
/// <para>HARD FLOOR — any of these forces <c>human_required</c> regardless of the declared policy: an
/// <see cref="DecisionTypes.ApproveAction"/> permission gate; any option flagged <see cref="DecisionOption.IsSideEffecting"/>
/// (an irreversible choice); <see cref="DecisionRiskLevels.High"/> risk; a MISSING recommended option or blocking reason
/// (an arbiter / auto-policy cannot answer responsibly without the raiser's recommendation + context). Past the floor,
/// a declared <c>auto_allowed</c> / <c>supervisor_first</c> survives verbatim; anything else (blank / unknown / already
/// human_required) falls to <c>human_required</c>.</para>
/// </summary>
public static class DecisionPolicyFloor
{
    /// <summary>The effective policy after the fail-closed floor — see the type doc. Pure + raise-only.</summary>
    public static string Effective(DecisionRequest request)
    {
        if (FloorsToHuman(request)) return DecisionPolicies.HumanRequired;

        // Past the floor, a recognized auto / supervisor policy survives; ANY other value (blank / unknown / already
        // human / a casing slip) fails closed to human. Case-insensitive so a "SupervisorFirst" casing slip resolves to
        // the canonical value rather than over-clamping — the floor above already caught every high-stakes case.
        if (Is(request.Policy, DecisionPolicies.AutoAllowed)) return DecisionPolicies.AutoAllowed;
        if (Is(request.Policy, DecisionPolicies.SupervisorFirst)) return DecisionPolicies.SupervisorFirst;

        return DecisionPolicies.HumanRequired;
    }

    /// <summary>
    /// True when the decision is high-stakes enough that ONLY a human may answer it, regardless of the declared policy.
    /// FAIL-CLOSED on the danger signals: an approve_action gate (case-insensitive), any side-effecting option, a risk
    /// that is NOT a recognized low/medium (so "high", "critical", a casing slip, blank, or any unknown all floor —
    /// mirroring the policy fail-closed default), or a missing recommendation / blocking reason. The auto / supervisor
    /// arbiter MUST NOT answer when this is true.
    /// </summary>
    public static bool FloorsToHuman(DecisionRequest request) =>
        Is(request.DecisionType, DecisionTypes.ApproveAction)
        || request.Options.Any(o => o.IsSideEffecting)
        || !RiskAllowsAuto(request.RiskLevel)
        || string.IsNullOrWhiteSpace(request.RecommendedOption)
        || string.IsNullOrWhiteSpace(request.BlockingReason);

    /// <summary>Only the recognized non-high tiers (low / medium, case-insensitive) permit an auto / supervisor answer. EVERYTHING else — high, an unknown / hallucinated synonym, a casing slip, blank — fails closed to "this needs a human".</summary>
    private static bool RiskAllowsAuto(string risk) =>
        Is(risk, DecisionRiskLevels.Low) || Is(risk, DecisionRiskLevels.Medium);

    private static bool Is(string? value, string canonical) => string.Equals(value, canonical, StringComparison.OrdinalIgnoreCase);
}
