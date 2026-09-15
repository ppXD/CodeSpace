using CodeSpace.Messages.Mediation;

namespace CodeSpace.Messages.Commands.Agents;

/// <summary>
/// Dispatch the agent-run reconciler sweep. The recurring job is its only sender; a test may send it
/// directly. Returns the count of abandoned runs the sweep recovered.
///
/// <para>NOT tenant-scoped — a system-wide recovery operation that runs without an actor context
/// (agent_run rows carry team_id; the sweep doesn't filter by tenant).</para>
///
/// <para>NOT transactional either (<see cref="INonTransactionalCommand"/>): the sweep is a batch of
/// independent runs, each recovered by its own epoch-fenced CAS write with its failure caught and retried
/// next tick, and its wait-recovery step refuses outright to run under an ambient transaction. One command
/// transaction around all of it would let a single bad run roll back every other run's recovery.</para>
/// </summary>
public sealed record ReconcileStuckAgentRunsCommand : ICommand<ReconcileStuckAgentRunsResponse>, INonTransactionalCommand;

/// <summary>Count returned for log surfacing + the recurring-job result.</summary>
public sealed record ReconcileStuckAgentRunsResponse
{
    public required int MarkedAbandonedFromRunning { get; init; }
}
