namespace CodeSpace.Core.Services.Agents.ModelCredentials;

/// <summary>
/// The PRODUCER of the objectively-probed capability tier for OPAQUE model ids — the hook the brain-tiering producer
/// (<c>IModelCapabilityTieringService</c>) leaves at <c>capability_tier = 'Unknown'</c> (an id it couldn't recognise).
/// For each such row it has THAT model run a fixed in-code micro-battery (<see cref="ModelCapabilityProbeBattery"/>) and
/// records a COARSE tier in <c>probed_capability_tier</c> — capped at Strong, monotonic-upgrade-only, graded in code
/// (never self-rated). A cached FACT driven by a recurring backfill job; never per-launch.
///
/// <para>Disjoint from the tiering producer by construction (it fires on <c>capability_tier IS NULL</c>, this on
/// <c>= 'Unknown'</c>), so they never fight over a row and the brain verdict is never overwritten. Fail-soft per row +
/// per team. The CONSUMER is the auto pick, which orders by the EFFECTIVE tier = probed ?? brain.</para>
/// </summary>
public interface IModelCapabilityProbeService
{
    /// <summary>Probe the team's OPAQUE (capability_tier='Unknown'), stale-or-unprobed pool models — run the battery, map the score to a coarse tier, write it as a monotonic upgrade. A no-op when the team has no such model; fail-soft on any error.</summary>
    Task ProbeTeamAsync(Guid teamId, CancellationToken cancellationToken);

    /// <summary>Backfill: probe EVERY team that has an opaque, stale-or-unprobed pool model, returning the number of teams processed (for log surfacing). Drives the recurring job.</summary>
    Task<int> ProbeAllPendingAsync(CancellationToken cancellationToken);
}
