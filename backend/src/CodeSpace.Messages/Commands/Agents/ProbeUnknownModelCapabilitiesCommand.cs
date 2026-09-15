using CodeSpace.Messages.Mediation;

namespace CodeSpace.Messages.Commands.Agents;

/// <summary>
/// Objectively probe the capability of every team's opaque (brain-tiered 'Unknown'), stale-or-unprobed pool model —
/// fired by the recurring capability-probe job; can also be sent ad-hoc from a test. NOT tenant-scoped: a system-wide
/// enrichment that runs without an actor context. Returns the number of teams processed for log surfacing.
///
/// <para>NOT transactional (<see cref="INonTransactionalCommand"/>): each team is probed over the network on its own, and one
/// whose provider fails keeps its recorded tier while the rest are still probed. One command transaction around the
/// whole tick would let a single bad row undo every other row's work.</para>
/// </summary>
public sealed record ProbeUnknownModelCapabilitiesCommand : ICommand<ProbeUnknownModelCapabilitiesResponse>, INonTransactionalCommand;

/// <summary>Count of teams whose opaque pool models the backfill probed (re-probes each across a days-long window).</summary>
public sealed record ProbeUnknownModelCapabilitiesResponse
{
    public required int TeamsProbed { get; init; }
}
