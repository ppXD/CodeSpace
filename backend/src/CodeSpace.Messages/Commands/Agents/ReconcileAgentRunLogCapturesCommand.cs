using CodeSpace.Messages.Mediation;

namespace CodeSpace.Messages.Commands.Agents;

/// <summary>Runs one bounded, system-wide recovery batch for AgentRun log capture health.</summary>
public sealed record ReconcileAgentRunLogCapturesCommand : ICommand<ReconcileAgentRunLogCapturesResponse>;

public sealed record ReconcileAgentRunLogCapturesResponse(int Claimed, int Completed, int CaptureFailed, int Superseded, int Retried, int LostLease)
{
    public int ExternalStateIndeterminate { get; init; }
}
