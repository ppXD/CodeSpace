using CodeSpace.Messages.Mediation;

namespace CodeSpace.Messages.Commands.Agents;

/// <summary>
/// Dispatch the agent-run reconciler sweep. Fired by the recurring job, but can also be sent ad-hoc
/// from an admin endpoint / tests. Returns the count of abandoned runs the sweep recovered.
///
/// <para>NOT tenant-scoped — a system-wide recovery operation that runs without an actor context
/// (agent_run rows carry team_id; the sweep doesn't filter by tenant).</para>
/// </summary>
public sealed record ReconcileStuckAgentRunsCommand : ICommand<ReconcileStuckAgentRunsResponse>;

/// <summary>Count returned for log surfacing + the recurring-job result.</summary>
public sealed record ReconcileStuckAgentRunsResponse
{
    public required int MarkedAbandonedFromRunning { get; init; }
}
