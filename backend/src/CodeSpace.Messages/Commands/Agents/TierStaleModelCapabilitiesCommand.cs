using CodeSpace.Messages.Mediation;

namespace CodeSpace.Messages.Commands.Agents;

/// <summary>
/// Backfill the cached capability tier for every team that has a not-yet-tiered enabled pool model — fired by the
/// recurring tiering job; can also be sent ad-hoc from a test. NOT tenant-scoped: a system-wide enrichment that runs
/// without an actor context. Returns the number of teams processed for log surfacing.
/// </summary>
public sealed record TierStaleModelCapabilitiesCommand : ICommand<TierStaleModelCapabilitiesResponse>;

/// <summary>Count of teams whose pending pool models the backfill tiered (0 in steady state).</summary>
public sealed record TierStaleModelCapabilitiesResponse
{
    public required int TeamsTiered { get; init; }
}
