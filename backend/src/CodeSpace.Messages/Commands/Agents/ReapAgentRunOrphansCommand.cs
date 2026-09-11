using CodeSpace.Messages.Mediation;

namespace CodeSpace.Messages.Commands.Agents;

/// <summary>
/// Settle the orphaned-resource cleanup receipts addressed to THIS host — the resources agent runs left behind when
/// they were abandoned by a reconciler sweep running on some other worker, which could not reach them. Fired by the
/// recurring reaper job; can also be sent ad-hoc from an admin path / tests.
///
/// <para>NOT tenant-scoped — system-wide host reclamation that runs without an actor context. Returns the count
/// actually reclaimed for log surfacing + the recurring-job result.</para>
/// </summary>
public sealed record ReapAgentRunOrphansCommand : ICommand<ReapAgentRunOrphansResponse>;

/// <summary>Count of orphaned resources this host reclaimed (the receipts that moved to <c>Compensated</c>).</summary>
public sealed record ReapAgentRunOrphansResponse
{
    public required int Compensated { get; init; }
}
