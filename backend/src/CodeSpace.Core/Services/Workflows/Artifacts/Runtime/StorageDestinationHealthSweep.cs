using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Messages.Dtos.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CodeSpace.Core.Services.Workflows.Artifacts.Runtime;

/// <summary>
/// Selects the destinations worth re-asking about, and asks.
///
/// <para><b>Destinations an Active route binds writes to, and destinations that still hold bytes.</b> A Draft profile
/// that no route names and that holds nothing is nobody's problem yet, and probing every profile a team ever made
/// would spend a real provider round trip on destinations no run can reach. Everything else is: the set this sweeps is
/// exactly the set whose failure loses data. That is why the profile's own lifecycle state gates neither arm —
/// disabling or retiring a profile unbinds no route and removes no bytes, so a Disabled destination is one whose every
/// write fails and whose every stored object is unwatched, which is precisely what a health card must not stay green
/// through.</para>
///
/// <para><b>The question follows why the destination is monitored.</b> An Active route requires proof that bytes land,
/// even when the profile lifecycle now makes that proof fail before provider I/O — that mismatch is precisely a broken
/// write path. A profile admitted only because it holds an unsettled placement is read-probed, including an Active
/// read-only legacy profile: lifecycle state alone does not make it a write destination. When both reasons apply, the
/// Active route wins. Read and write evidence remain distinct through <c>WriteVerified</c>.</para>
/// </summary>
public sealed class StorageDestinationHealthSweep : IStorageDestinationHealthSweep
{
    /// <summary>
    /// How stale an observation may be before it is re-taken. Deliberately BELOW the job's interval so every tick
    /// re-probes rather than skipping on clock jitter — the precedent's 30-minute window over a 15-minute tick suits a
    /// probe that only fills an unknown, and this one exists to notice a destination that WAS working and stopped.
    /// </summary>
    internal static readonly TimeSpan StaleAfter = TimeSpan.FromMinutes(10);

    /// <summary>
    /// How many destinations one pass probes. The population only ever GROWS — Retired is terminal, and a placement
    /// leaves it only by being purged or deleted — so an unbounded pass spends one more provider round trip for every
    /// destination the deployment ever adds. Capping degrades coverage to "slower" instead, which the
    /// oldest-observation-first ordering makes fair rather than arbitrary.
    ///
    /// <para><b>It bounds the count, not the clock.</b> No probe carries a timeout and nothing refuses to start a pass
    /// while one is running, so a pass whose destinations all block on a dead provider still outruns the tick and
    /// overlaps the next. Bounding a pass in TIME — a pass deadline, a per-probe timeout, a concurrency guard — is a
    /// separate change this cap does not make; what it buys is a bounded number of probes per pass.</para>
    ///
    /// <para>So on a deployment whose destinations answer, a pass covers up to 200 of them and a larger population is
    /// covered in ceil(count / 200) passes — a description of the healthy case, not a guarantee about a sick one.</para>
    /// </summary>
    internal const int MaxPerPass = 200;

    private readonly CodeSpaceDbContext _db;
    private readonly IStorageProfileProbeService _probe;
    private readonly TimeProvider _clock;
    private readonly ILogger<StorageDestinationHealthSweep> _logger;

    public StorageDestinationHealthSweep(CodeSpaceDbContext db, IStorageProfileProbeService probe, TimeProvider clock, ILogger<StorageDestinationHealthSweep> logger)
    {
        _db = db;
        _probe = probe;
        _clock = clock;
        _logger = logger;
    }

    public async Task<int> ProbeStaleAsync(CancellationToken cancellationToken)
    {
        var due = await StaleMonitoredDestinationsAsync(cancellationToken).ConfigureAwait(false);

        foreach (var destination in due)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await ProbeOneAsync(destination, cancellationToken).ConfigureAwait(false);
        }

        return due.Count;
    }

    private async Task<IReadOnlyList<MonitoredDestination>> StaleMonitoredDestinationsAsync(CancellationToken cancellationToken) =>
        await StaleDestinations(Tables(), _clock.GetUtcNow() - StaleAfter).ToListAsync(cancellationToken).ConfigureAwait(false);

    /// <summary>
    /// The monitored destinations whose health is missing or older than <see cref="StaleAfter"/>, oldest observation
    /// first, bounded to <see cref="MaxPerPass"/>.
    ///
    /// <para>Missing health is included on purpose: a hand-built route reaches Active with nothing having probed it at
    /// all, and that is precisely the destination most likely to be wrong.</para>
    ///
    /// <para>The profile's own lifecycle state is deliberately not a filter here — see the type doc.</para>
    ///
    /// <para><b>Never observed first, then longest unobserved, bounded to <see cref="MaxPerPass"/>.</b> The bound is
    /// what keeps a growing population from spending an unbounded number of round trips on one pass, and the ordering
    /// is what keeps the bound from starving the tail: a destination too far back is covered LATER, never never. Nulls
    /// are ordered first explicitly rather than left to the provider, because PostgreSQL sorts NULLs last on an
    /// ascending key and would put exactly the destinations nothing has ever contacted at the very back. Among those, newest-created goes first
    /// — there is no observation age to rank them by, and the newest is the one an operator just made and is watching;
    /// nothing is starved by that choice because a probe records a row and so leaves the never-observed set for good.</para>
    ///
    /// <para>The <c>observedAt != null</c> key looks redundant and is not: LINQ-to-Objects already sorts nulls first,
    /// so every in-memory test passes with it deleted, and only the integration tier — which asks PostgreSQL — can
    /// tell. Delete it and the never-probed queue behind every destination that has a row, which under a capped pass
    /// means never.</para>
    ///
    /// <para>Internal and composed over <see cref="PopulationTables"/> rather than the context, so which destinations
    /// the sweep considers its business — and in which order — is pinned directly by unit tests (InternalsVisibleTo).
    /// The bound deliberately takes no parameter: a seam that let a caller pass its own would leave the ceiling a pass
    /// actually runs under asserted by nothing.</para>
    /// </summary>
    internal static IQueryable<MonitoredDestination> StaleDestinations(PopulationTables tables, DateTimeOffset cutoff)
    {
        var writeDestinations = BoundByAnActiveRoute(tables);
        var monitored = writeDestinations.Union(HoldingUnsettledPlacements(tables));

        return (from profile in tables.Profiles
            join health in tables.Health
                on new { profile.TeamId, StorageProfileId = profile.Id }
                equals new { health.TeamId, health.StorageProfileId } into healthRows
            from health in healthRows.DefaultIfEmpty()
            let observedAt = health == null ? (DateTimeOffset?)null : health.ObservedAt
            where monitored.Contains(profile.Id)
                && (observedAt == null || observedAt < cutoff)
            orderby observedAt != null, observedAt, profile.CreatedDate descending
            select new MonitoredDestination(profile.TeamId, profile.Id, writeDestinations.Contains(profile.Id)))
            .Take(MaxPerPass);
    }

    /// <summary>Every profile the current revision of an Active route names — the destination a run's next write lands on.</summary>
    private static IQueryable<Guid> BoundByAnActiveRoute(PopulationTables tables) =>
        from route in tables.Routes
        join routeRevision in tables.RouteRevisions
            on new { route.TeamId, StorageRouteId = route.Id, Revision = route.CurrentRevision }
            equals new { routeRevision.TeamId, routeRevision.StorageRouteId, routeRevision.Revision }
        where route.State == StorageRouteState.Active
        select routeRevision.StorageProfileId;

    /// <summary>
    /// Every profile that still holds a placement nothing has settled. <c>Purged</c> and <c>Deleted</c> are the only
    /// states that release a destination — the same population the retirement guard counts, because a <c>Missing</c>
    /// or <c>Corrupt</c> row is still a record of bytes there.
    ///
    /// <para>Asked as an EXISTS from the revision side, so the arm costs one row per profile revision rather than one
    /// per placement, and correlated on team as well as revision so it seeks
    /// <c>ux_artifact_location_profile_object_key</c> by its leading column. It runs deployment-wide on every tick.</para>
    ///
    /// <para><b>Admits per revision, but the probe still asks the CURRENT one.</b> This arm scans every revision, so a
    /// profile re-pointed at a new destination is admitted because bytes sit under revision 3 while <c>ProbeOneAsync</c>
    /// passes no revision and <c>StorageProfileProbeTargetResolver</c> resolves that to the profile's
    /// <c>CurrentRevision</c> — the destination that got it in is NOT the one contacted. Health is one row per profile
    /// (<c>StorageProfileHealth</c>), so a per-revision answer has nowhere to be written; making health per-revision is
    /// the change that closes this, and it is bigger than a sweep. Until then the recorded observation describes the
    /// profile's current destination only, and a health row's <c>ProfileRevision</c> is what says which that was.</para>
    /// </summary>
    private static IQueryable<Guid> HoldingUnsettledPlacements(PopulationTables tables)
    {
        var unsettled = tables.Locations.Where(location =>
            location.State != ArtifactLocationState.Purged && location.State != ArtifactLocationState.Deleted);

        return from revision in tables.ProfileRevisions
            where unsettled.Any(location => location.TeamId == revision.TeamId && location.StorageProfileRevisionId == revision.Id)
            select revision.StorageProfileId;
    }

    /// <summary>
    /// One destination, asked the strongest question it still admits — see <see cref="MonitoredDestination.VerifyWrite"/>.
    ///
    /// <para>A probe that throws is logged and the sweep continues: one unreachable provider must not stop every other
    /// team's destination from being checked, and the throw itself carries no observation to record.</para>
    /// </summary>
    private async Task ProbeOneAsync(MonitoredDestination destination, CancellationToken cancellationToken)
    {
        try
        {
            var result = await _probe.ProbeAsync(
                new StorageProfileProbeRequest(destination.TeamId, destination.StorageProfileId, ProfileRevision: null, VerifyWriteAccess: destination.VerifyWrite), cancellationToken)
                .ConfigureAwait(false);

            if (result.Status == StorageProfileProbeStatusValue.Available) return;

            _logger.LogWarning("Scheduled probe of storage profile {ProfileId} for team {TeamId} answered {Status} ({Stage}/{Code}) with write verification {VerifyWrite}; this destination either binds writes for an Active route or still holds stored objects",
                destination.StorageProfileId, destination.TeamId, result.Status, result.Failure?.Stage, result.Failure?.Code, destination.VerifyWrite);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Scheduled probe of storage profile {ProfileId} for team {TeamId} could not be completed; its recorded health is unchanged and the next pass retries",
                destination.StorageProfileId, destination.TeamId);
        }
    }

    private PopulationTables Tables() => new()
    {
        Profiles = _db.StorageProfile,
        Routes = _db.StorageRoute,
        RouteRevisions = _db.StorageRouteRevision,
        ProfileRevisions = _db.StorageProfileRevision,
        Locations = _db.ArtifactLocation,
        Health = _db.StorageProfileHealth,
    };

    /// <summary>
    /// The tables the population is read from. No <c>AsNoTracking</c>: the population projects to
    /// <see cref="MonitoredDestination"/>, so no entity is ever materialized to track.
    /// </summary>
    internal sealed record PopulationTables
    {
        public required IQueryable<StorageProfile> Profiles { get; init; }
        public required IQueryable<StorageRoute> Routes { get; init; }
        public required IQueryable<StorageRouteRevision> RouteRevisions { get; init; }
        public required IQueryable<StorageProfileRevision> ProfileRevisions { get; init; }
        public required IQueryable<ArtifactLocation> Locations { get; init; }
        public required IQueryable<StorageProfileHealth> Health { get; init; }
    }

    /// <summary>
    /// One destination and the question its production dependency asks. <paramref name="VerifyWrite"/> is true only
    /// when an Active route's current revision names the profile; merely holding an unsettled placement asks a read.
    /// If both are true, write evidence wins because the route will send the next bytes here.
    /// </summary>
    internal sealed record MonitoredDestination(Guid TeamId, Guid StorageProfileId, bool VerifyWrite);
}
