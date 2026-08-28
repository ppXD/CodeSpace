using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Messages.Dtos.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CodeSpace.Core.Services.Workflows.Artifacts.Runtime;

/// <summary>
/// Selects the destinations worth re-asking about, and asks.
///
/// <para><b>Only destinations an Active route binds writes to.</b> A Draft profile that cannot be written is nobody's
/// problem yet, and probing every profile a team ever made would spend a real provider round trip on destinations no
/// run can reach. The set this sweeps is exactly the set whose failure loses data.</para>
///
/// <para><b>A write probe, not a reachability ping.</b> A read-only probe qualifies the credential's ability to list;
/// it does not qualify that a run's bytes will land, which is the only claim worth scheduling. The probe writes and
/// discards one object per destination per pass.</para>
/// </summary>
public sealed class StorageDestinationHealthSweep : IStorageDestinationHealthSweep
{
    /// <summary>
    /// How stale an observation may be before it is re-taken. Deliberately BELOW the job's interval so every tick
    /// re-probes rather than skipping on clock jitter — the precedent's 30-minute window over a 15-minute tick suits a
    /// probe that only fills an unknown, and this one exists to notice a destination that WAS working and stopped.
    /// </summary>
    internal static readonly TimeSpan StaleAfter = TimeSpan.FromMinutes(10);

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
        var due = await StaleBoundDestinationsAsync(cancellationToken).ConfigureAwait(false);

        foreach (var destination in due)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await ProbeOneAsync(destination, cancellationToken).ConfigureAwait(false);
        }

        return due.Count;
    }

    /// <summary>
    /// Every profile an Active route's current revision names, whose health is missing or older than
    /// <see cref="StaleAfter"/>.
    ///
    /// <para>Missing health is included on purpose: a hand-built route reaches Active with nothing having probed it at
    /// all, and that is precisely the destination most likely to be wrong.</para>
    /// </summary>
    private async Task<IReadOnlyList<BoundDestination>> StaleBoundDestinationsAsync(CancellationToken cancellationToken)
    {
        var cutoff = _clock.GetUtcNow() - StaleAfter;

        return await (
            from route in _db.StorageRoute.AsNoTracking()
            join routeRevision in _db.StorageRouteRevision.AsNoTracking()
                on new { route.TeamId, StorageRouteId = route.Id, Revision = route.CurrentRevision }
                equals new { routeRevision.TeamId, routeRevision.StorageRouteId, routeRevision.Revision }
            join profile in _db.StorageProfile.AsNoTracking()
                on new { routeRevision.TeamId, Id = routeRevision.StorageProfileId }
                equals new { profile.TeamId, profile.Id }
            join health in _db.StorageProfileHealth.AsNoTracking()
                on new { profile.TeamId, StorageProfileId = profile.Id }
                equals new { health.TeamId, health.StorageProfileId } into healthRows
            from health in healthRows.DefaultIfEmpty()
            where route.State == StorageRouteState.Active
                && profile.State == StorageProfileState.Active
                && (health == null || health.ObservedAt < cutoff)
            select new BoundDestination(profile.TeamId, profile.Id))
            .Distinct()
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// One destination. A probe that throws is logged and the sweep continues: one unreachable provider must not stop
    /// every other team's destination from being checked, and the throw itself carries no observation to record.
    /// </summary>
    private async Task ProbeOneAsync(BoundDestination destination, CancellationToken cancellationToken)
    {
        try
        {
            var result = await _probe.ProbeAsync(
                new StorageProfileProbeRequest(destination.TeamId, destination.StorageProfileId, ProfileRevision: null, VerifyWriteAccess: true), cancellationToken)
                .ConfigureAwait(false);

            if (result.Status == StorageProfileProbeStatusValue.Available) return;

            _logger.LogWarning("Scheduled probe of storage profile {ProfileId} for team {TeamId} answered {Status} ({Stage}/{Code}); this destination currently binds writes for an Active route",
                destination.StorageProfileId, destination.TeamId, result.Status, result.Failure?.Stage, result.Failure?.Code);
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

    private sealed record BoundDestination(Guid TeamId, Guid StorageProfileId);
}
