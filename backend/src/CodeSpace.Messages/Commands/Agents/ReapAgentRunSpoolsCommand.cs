using CodeSpace.Messages.Mediation;

namespace CodeSpace.Messages.Commands.Agents;

/// <summary>
/// Reclaim the on-disk spool (out.log / err.log / exit / pid) of agent runs that finished long enough ago
/// that their durable output is no longer needed for recovery or re-attach — only terminal runs are touched,
/// and their redacted output is already in the durable event log. Fired by the recurring reaper job; can also
/// be sent ad-hoc from an admin path / tests.
///
/// <para>NOT tenant-scoped — system-wide disk reclamation that runs without an actor context. Returns the
/// count reaped for log surfacing + the recurring-job result.</para>
/// </summary>
public sealed record ReapAgentRunSpoolsCommand : ICommand<ReapAgentRunSpoolsResponse>;

/// <summary>Count of terminal-run spool directories reclaimed by the sweep.</summary>
public sealed record ReapAgentRunSpoolsResponse
{
    public required int Reaped { get; init; }
}
