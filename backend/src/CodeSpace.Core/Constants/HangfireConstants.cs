namespace CodeSpace.Core.Constants;

public static class HangfireConstants
{
    /// <summary>The CONTROL-PLANE queue: short jobs — the engine run walk, wait/resume, recurring reconcilers/expiry, webhook registration. A dedicated worker pool processes it so a saturated agent pool can never starve them.</summary>
    public const string DefaultQueue = "default";

    /// <summary>The AGENT queue: the long-running <c>IAgentRunExecutor</c> jobs (an agent.run run can hold a worker for minutes while a codex/claude child runs). Isolated onto its own worker pool so it can't starve the control plane. Pinned by a test (Rule 8).</summary>
    public const string AgentQueue = "agents";
}
