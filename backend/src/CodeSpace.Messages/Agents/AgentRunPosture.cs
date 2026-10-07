namespace CodeSpace.Messages.Agents;

/// <summary>
/// The sandbox posture of the agent run a tool call serves: its autonomy tier, the permissions its own sandbox was
/// launched with, and the repositories it is bound to. A tool that starts a sandbox of its own on the run's behalf
/// (<c>agent.run_command</c>) runs it no wider than this, so a tool call can never reach a network, a host or a resource
/// ceiling the calling agent was denied — nor a repository, or a ref of read-only context, its run was never bound to.
/// Stamped by the run's MCP endpoint from the run's task; absent off the agent-tool path, where a workflow node keeps its
/// own authored posture.
/// </summary>
public sealed record AgentRunPosture
{
    /// <summary>The run this posture is of — the unit its commands queue by, so one run never has two running at once.</summary>
    public Guid RunId { get; init; }

    /// <summary>The run's autonomy tier — what its resource ceilings derive from.</summary>
    public required AgentAutonomyLevel Autonomy { get; init; }

    /// <summary>The run's effective permissions — its network and egress allowlist.</summary>
    public required AgentPermissions Permissions { get; init; }

    /// <summary>
    /// The repositories the run is bound to — its workspace's repositories, each with its access and ref — and the only
    /// ones a tool call may name. Empty (the default) binds none: a posture stamped without a binding reaches no
    /// repository, never every repository of the team.
    /// </summary>
    public IReadOnlyList<WorkspaceRepositorySpec> Repositories { get; init; } = [];
}
