namespace CodeSpace.Messages.Agents;

/// <summary>
/// The sandbox posture of the agent run a tool call serves: its autonomy tier and the permissions its own sandbox was
/// launched with. A tool that starts a sandbox of its own on the run's behalf (<c>agent.run_command</c>) runs it no
/// wider than this, so a tool call can never reach a network, a host or a resource ceiling the calling agent was
/// denied. Stamped by the run's MCP endpoint from the run's task; absent off the agent-tool path, where a workflow node
/// keeps its own authored posture.
/// </summary>
public sealed record AgentRunPosture
{
    /// <summary>The run this posture is of — the unit its commands queue by, so one run never has two running at once.</summary>
    public Guid RunId { get; init; }

    /// <summary>The run's autonomy tier — what its resource ceilings derive from.</summary>
    public required AgentAutonomyLevel Autonomy { get; init; }

    /// <summary>The run's effective permissions — its network and egress allowlist.</summary>
    public required AgentPermissions Permissions { get; init; }
}
