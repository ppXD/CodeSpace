namespace CodeSpace.Messages.Agents;

/// <summary>
/// The governance verdict for one agent tool call under a run's autonomy tier: run it now, require human approval
/// first, or refuse it outright. Fail-closed — the most-restrictive tier (and any unknown one) DENIES a gated tool.
/// </summary>
public enum AgentToolGateDecision
{
    /// <summary>Run the tool now, no approval needed (a read-only / explicitly un-gated tool, or a trusted-enough tier).</summary>
    Allow,

    /// <summary>The tool is permitted only with a human in the loop — route it through the approval pipeline before running.</summary>
    RequireApproval,

    /// <summary>The tool is not permitted at this autonomy tier — refuse it.</summary>
    Deny,
}
