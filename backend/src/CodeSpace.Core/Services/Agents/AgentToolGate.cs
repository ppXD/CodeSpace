using CodeSpace.Messages.Agents;

namespace CodeSpace.Core.Services.Agents;

/// <summary>
/// The single source of truth for "autonomy tier × tool risk → governance verdict" — the per-call gate the MCP
/// server (and the future native loop) consult before running a model-requested tool. It composes with
/// <see cref="AgentAutonomyPolicy"/> (which maps the SAME tiers to sandbox network/write knobs): that decides where
/// the run may act, this decides whether a given tool call may proceed.
///
/// <para>A tool that does not require approval (read-only / explicitly un-gated) always runs. A gated (destructive)
/// tool runs only as the tier permits: Confined refuses it, Standard and Trusted require a human in the loop, and
/// only Unleashed runs it unattended. Fail-closed — an unknown tier refuses a gated tool. An ALWAYS-approve tool
/// (<see cref="IAgentTool.AlwaysRequiresApproval"/>, e.g. an irreversible merge) tightens only the Unleashed cell:
/// it escalates that tier's Allow to RequireApproval so the tool can never auto-run — every other tier already
/// asks (Standard/Trusted) or refuses (Confined). The mapping is pinned by a unit test so any change is a visible,
/// reviewed decision (Rule 8 spirit).</para>
/// </summary>
public static class AgentToolGate
{
    /// <summary>Decide whether a tool that does (or does not) require approval may run under a run's autonomy tier. An always-approve tool can never reach Allow — at Unleashed it escalates to RequireApproval instead.</summary>
    public static AgentToolGateDecision Decide(AgentAutonomyLevel level, bool requiresApproval, bool alwaysRequiresApproval = false)
    {
        if (!requiresApproval) return AgentToolGateDecision.Allow;   // a read-only / un-gated tool always runs

        return level switch
        {
            AgentAutonomyLevel.Standard => AgentToolGateDecision.RequireApproval,
            AgentAutonomyLevel.Trusted => AgentToolGateDecision.RequireApproval,
            AgentAutonomyLevel.Unleashed => alwaysRequiresApproval ? AgentToolGateDecision.RequireApproval : AgentToolGateDecision.Allow,
            _ => AgentToolGateDecision.Deny,   // Confined + any unknown tier → refuse a gated tool (fail-closed)
        };
    }
}
