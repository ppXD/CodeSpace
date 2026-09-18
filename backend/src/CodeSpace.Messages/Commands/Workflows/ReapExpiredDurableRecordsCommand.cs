using CodeSpace.Messages.Mediation;
using CodeSpace.Messages.Retention;

namespace CodeSpace.Messages.Commands.Workflows;

/// <summary>
/// Run one bounded durable-retention sweep: for every registered plane, claim records past their class's age floor,
/// establish whether anything still cites each one, and reclaim only those proven uncited past both waits.
///
/// <para>NOT tenant-scoped — system-wide reclamation that runs without an actor context. Fired by the recurring
/// reaper job; also sendable ad-hoc from an admin path or a test.</para>
///
/// <para>NOT transactional (<see cref="INonTransactionalCommand"/>): each record is settled by its own conditional
/// write, and a reclamation that fails leaves only that record for the next pass. One command transaction around the
/// whole tick would let a single unreachable destination undo every other record's work — and it would hold a
/// transaction open across provider I/O.</para>
/// </summary>
public sealed record ReapExpiredDurableRecordsCommand : ICommand<ReapExpiredDurableRecordsResponse>, INonTransactionalCommand;

/// <summary>The sweep's per-bucket counts, surfaced for logging and for the recurring job's result.</summary>
public sealed record ReapExpiredDurableRecordsResponse
{
    public required DurableRetentionSweepSummary Summary { get; init; }
}
