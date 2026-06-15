using CodeSpace.Messages.Mediation;

namespace CodeSpace.Messages.Commands.Agents;

/// <summary>
/// Durably expire every stale UNDECIDED supervisor decision (still Pending past the retention window), so a stale row
/// gets a clean terminal instead of lingering Pending forever. Dispatched by the recurring reaper job (only registered
/// when the supervisor lane is enabled); can also be sent ad-hoc (admin path / tests).
///
/// <para>NOT tenant-scoped — a system-wide internal sweep with no actor context. Finds no rows when the lane is off
/// (the table is empty). Returns the count expired for log surfacing + the recurring-job result.</para>
/// </summary>
public sealed record ExpireStaleSupervisorDecisionsCommand : ICommand<ExpireStaleSupervisorDecisionsResponse>;

/// <summary>Count of stale Pending supervisor decisions the reaper durably flipped to Expired this tick (the ledger-CAS winners).</summary>
public sealed record ExpireStaleSupervisorDecisionsResponse
{
    public required int Expired { get; init; }
}
