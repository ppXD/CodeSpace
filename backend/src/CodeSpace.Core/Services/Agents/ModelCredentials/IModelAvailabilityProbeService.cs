namespace CodeSpace.Core.Services.Agents.ModelCredentials;

/// <summary>
/// The PRODUCER of the cached model AVAILABILITY (reachability) flag — a SEPARATE axis from the capability tier
/// (<c>IModelCapabilityTieringService</c>). It pings each ENABLED self-hosted Custom-gateway model (Provider='Custom'
/// with a base_url) with a minimal completion and records whether the endpoint RESPONDED: a reply on any HTTP status
/// (incl. auth / rate-limit / shape errors — those are reachability-orthogonal) ⇒ <c>available=true</c>; only a genuine
/// no-response transport failure (connection refused / reset / DNS / client timeout) ⇒ <c>available=false</c>. Vendor
/// models are never pinged (trust the vendor; bound the live-call cost to the gateways that actually go down) and stay
/// NULL = assumed available.
///
/// <para>A cached FACT driven by a recurring backfill job — never run per-launch. Fail-soft per row (one dead gateway
/// never aborts the batch) and per team (a DB / query fault leaves availability unchanged and the pool byte-identical).
/// The CONSUMER is the unpinned auto pick in <c>ModelPoolSelector</c> (a SOFT preference with an anti-strand fallback).</para>
/// </summary>
public interface IModelAvailabilityProbeService
{
    /// <summary>Probe the team's ENABLED Custom-gateway pool models whose availability is stale (or never probed) and write each verdict to <c>available</c> + <c>last_pinged_at</c>. A no-op when the team has no such model; fail-soft on any error.</summary>
    Task ProbeTeamAsync(Guid teamId, CancellationToken cancellationToken);

    /// <summary>Backfill: probe EVERY team that has a stale-or-unprobed enabled Custom-gateway pool model, returning the number of teams processed (for log surfacing). Drives the recurring job; steady-state re-probes each gateway once per back-off window.</summary>
    Task<int> ProbeAllPendingAsync(CancellationToken cancellationToken);
}
