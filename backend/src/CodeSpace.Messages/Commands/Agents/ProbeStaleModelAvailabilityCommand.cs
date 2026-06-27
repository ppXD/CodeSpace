using CodeSpace.Messages.Mediation;

namespace CodeSpace.Messages.Commands.Agents;

/// <summary>
/// Re-probe the cached availability of every team's stale-or-unprobed enabled Custom-gateway pool model — fired by the
/// recurring availability job; can also be sent ad-hoc from a test. NOT tenant-scoped: a system-wide enrichment that
/// runs without an actor context. Returns the number of teams processed for log surfacing.
/// </summary>
public sealed record ProbeStaleModelAvailabilityCommand : ICommand<ProbeStaleModelAvailabilityResponse>;

/// <summary>Count of teams whose Custom-gateway pool models the backfill probed (re-probes each gateway once per back-off window).</summary>
public sealed record ProbeStaleModelAvailabilityResponse
{
    public required int TeamsProbed { get; init; }
}
