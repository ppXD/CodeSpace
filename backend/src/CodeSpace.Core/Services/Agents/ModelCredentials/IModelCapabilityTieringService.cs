namespace CodeSpace.Core.Services.Agents.ModelCredentials;

/// <summary>
/// The PRODUCER of the cached capability tier (the consumer is <c>CapabilityCatalog.Render</c>): asks the brain to tier
/// a team's not-yet-tiered pool model ids in ONE structured call and writes the verdict to <c>capability_tier</c> +
/// <c>last_tiered_at</c>. A cached FACT keyed on the model id — never run per-launch (a recurring backfill job drives it),
/// so the tiering call's non-determinism is frozen in the column, not in any run.
/// Fail-closed: any miss (no structured provider / no pool model / a degraded reply) leaves the rows un-tiered (NULL) —
/// the catalog renders byte-identically and the brain allocates blind, exactly as before this signal existed.
/// </summary>
public interface IModelCapabilityTieringService
{
    /// <summary>Tier the team's ENABLED, not-yet-tiered (capability_tier IS NULL) pool models in one structured call. A no-op when the team has nothing to tier or no structured client resolves; fail-closed on any error.</summary>
    Task TierTeamAsync(Guid teamId, CancellationToken cancellationToken);

    /// <summary>Backfill: tier EVERY team that has a not-yet-tiered enabled pool model (one structured call per team), returning the number of teams processed (for log surfacing). Drives the recurring job; steady-state returns 0 (already-tiered rows are skipped).</summary>
    Task<int> TierAllPendingAsync(CancellationToken cancellationToken);
}
