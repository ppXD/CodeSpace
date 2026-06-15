using CodeSpace.Core.Services.Agents;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Dtos.Agents;

namespace CodeSpace.Core.Services.Supervisor;

/// <summary>
/// The supervisor's governance gate (PR-E E5, Rule 7 — REUSES <see cref="AgentToolGate"/>, the SAME per-call
/// gate the MCP tool fabric consults; it is NOT reinvented). It decides whether a supervisor DECISION's side
/// effect may fire: a SIDE-EFFECTING decision (<c>spawn</c> / <c>retry</c> — they create agent runs) is gated;
/// a read-only / terminal decision (<c>plan</c> / <c>merge</c> / <c>stop</c> / <c>ask_human</c>) always proceeds.
///
/// <para>The operator's <see cref="SupervisorApprovalPolicy"/> maps to the autonomy TIER the gate reads
/// (<see cref="ToAutonomyLevel"/>): <c>None</c> → <see cref="AgentAutonomyLevel.Unleashed"/> (autonomous → Allow),
/// <c>Spawns</c> → <see cref="AgentAutonomyLevel.Standard"/> (a human approves first → RequireApproval). FAIL-CLOSED:
/// an unmapped policy falls to <see cref="AgentAutonomyLevel.Confined"/> so the gate DENIES the side effect (never
/// a silent auto-run). The mapping is pinned by a unit test (Rule 8 spirit) so a new policy without a tier is a
/// visible decision. An IRREVERSIBLE decision (a future merge-PR / push) sets <c>alwaysRequiresApproval</c> so even
/// the most permissive tier escalates Allow → RequireApproval — no auto-run of a destructive effect.</para>
/// </summary>
public static class SupervisorGovernance
{
    /// <summary>Whether a decision kind has a SIDE EFFECT the gate governs (creates agent runs). Plan/merge/stop/ask_human are read-only / terminal / already-human and pass through ungated.</summary>
    public static bool IsSideEffecting(string decisionKind) =>
        decisionKind is SupervisorDecisionKinds.Spawn or SupervisorDecisionKinds.Retry;

    /// <summary>
    /// The governance verdict for a decision under the run's approval policy. A non-side-effecting decision is
    /// always <see cref="AgentToolGateDecision.Allow"/> (nothing to gate). A side-effecting one routes through
    /// <see cref="AgentToolGate.Decide"/> with the policy-derived tier: Allow → run the side effect now;
    /// RequireApproval → park for a human before it; Deny → refuse it (fail-closed). <paramref name="irreversible"/>
    /// forwards to the gate's always-approve cell (reserved for a future merge-PR / push).
    /// </summary>
    public static AgentToolGateDecision Decide(string decisionKind, SupervisorApprovalPolicy policy, bool irreversible = false)
    {
        if (!IsSideEffecting(decisionKind)) return AgentToolGateDecision.Allow;

        return AgentToolGate.Decide(ToAutonomyLevel(policy), requiresApproval: true, alwaysRequiresApproval: irreversible);
    }

    /// <summary>Map the operator's approval policy to the autonomy tier the gate reads. FAIL-CLOSED default: an unmapped policy → Confined → the gate DENIES the side effect.</summary>
    public static AgentAutonomyLevel ToAutonomyLevel(SupervisorApprovalPolicy policy) => policy switch
    {
        SupervisorApprovalPolicy.None => AgentAutonomyLevel.Unleashed,    // autonomous → Allow
        SupervisorApprovalPolicy.Spawns => AgentAutonomyLevel.Standard,   // human approves first → RequireApproval
        _ => AgentAutonomyLevel.Confined,                                  // unknown policy → Deny (fail-closed)
    };
}
