using CodeSpace.Messages.Mediation;

namespace CodeSpace.Messages.Commands.Decisions;

/// <summary>
/// Apply the configured default to every undecided agent-grain decision whose deadline has passed (Decision substrate
/// D5b — AC4 never-hang), so a stranded agent gets its default answer instead of hanging forever. Dispatched by the
/// recurring decision reaper each minute; can also be sent ad-hoc (admin path / tests).
///
/// <para>NOT tenant-scoped — a system-wide internal sweep with no actor context. Finds no rows when nothing is parked (a
/// cheap no-op). Returns the count durably defaulted for log surfacing + the recurring-job result.</para>
///
/// <para>NOT transactional (<see cref="INonTransactionalCommand"/>): each decision is settled by its own status-guarded
/// CAS and commits on its own. One transaction over the tick held every row it touched until the tick committed, and the
/// sweep walks decisions by deadline while a run's end locks its own by id — so a sweep holding one of a run's decisions
/// and an end holding another deadlocked, and the end lost its real result to it.</para>
/// </summary>
public sealed record ExpireStaleDecisionsCommand : ICommand<ExpireStaleDecisionsResponse>, INonTransactionalCommand;

/// <summary>Count of undecided decisions the reaper durably answered with their default this tick (the ledger-CAS winners).</summary>
public sealed record ExpireStaleDecisionsResponse
{
    public required int Defaulted { get; init; }
}
