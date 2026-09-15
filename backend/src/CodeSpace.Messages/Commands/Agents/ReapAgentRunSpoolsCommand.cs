using CodeSpace.Messages.Mediation;

namespace CodeSpace.Messages.Commands.Agents;

/// <summary>
/// Reclaim the on-disk spool (out.log / err.log / exit / pid) of agent runs that finished long enough ago
/// that their durable output is no longer needed for recovery or re-attach — only terminal runs are touched,
/// and their redacted output is already in the durable event log. The recurring reaper job is its only sender;
/// a test may send it directly.
///
/// <para>NOT tenant-scoped — system-wide disk reclamation that runs without an actor context. Returns the
/// count reaped for log surfacing + the recurring-job result.</para>
///
/// <para>NOT transactional (<see cref="INonTransactionalCommand"/>): it locks each candidate in a transaction of its OWN
/// before touching the filesystem — a transaction the pipeline's would refuse to nest. One command transaction around
/// the whole tick would let a single bad row undo every other row's work.</para>
/// </summary>
public sealed record ReapAgentRunSpoolsCommand : ICommand<ReapAgentRunSpoolsResponse>, INonTransactionalCommand;

/// <summary>Count of terminal-run spool directories reclaimed by the sweep.</summary>
public sealed record ReapAgentRunSpoolsResponse
{
    public required int Reaped { get; init; }
}
