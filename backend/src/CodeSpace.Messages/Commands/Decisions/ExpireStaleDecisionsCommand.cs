using CodeSpace.Messages.Mediation;

namespace CodeSpace.Messages.Commands.Decisions;

/// <summary>
/// Apply the configured default to every undecided agent-grain decision whose deadline has passed (Decision substrate
/// D5b — AC4 never-hang), so a stranded agent gets its default answer instead of hanging forever. Dispatched by the
/// recurring decision reaper each minute; can also be sent ad-hoc (admin path / tests).
///
/// <para>NOT tenant-scoped — a system-wide internal sweep with no actor context. Finds no rows when nothing is parked (a
/// cheap no-op). Returns the count durably defaulted for log surfacing + the recurring-job result.</para>
/// </summary>
public sealed record ExpireStaleDecisionsCommand : ICommand<ExpireStaleDecisionsResponse>;

/// <summary>Count of undecided decisions the reaper durably answered with their default this tick (the ledger-CAS winners).</summary>
public sealed record ExpireStaleDecisionsResponse
{
    public required int Defaulted { get; init; }
}
