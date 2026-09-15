using CodeSpace.Messages.Mediation;

namespace CodeSpace.Messages.Commands.Agents;

/// <summary>
/// Runs one bounded, system-wide recovery batch for AgentRun log capture health.
///
/// <para>NOT transactional (<see cref="INonTransactionalCommand"/>): each capture is claimed under its own recovery lease
/// with SKIP LOCKED and settled independently, on the service's own connection. One command transaction around the
/// whole tick would let a single bad row undo every other row's work.</para>
/// </summary>
public sealed record ReconcileAgentRunLogCapturesCommand : ICommand<ReconcileAgentRunLogCapturesResponse>, INonTransactionalCommand;

public sealed record ReconcileAgentRunLogCapturesResponse(int Claimed, int Completed, int CaptureFailed, int Superseded, int Retried, int LostLease)
{
    public int ExternalStateIndeterminate { get; init; }
}
