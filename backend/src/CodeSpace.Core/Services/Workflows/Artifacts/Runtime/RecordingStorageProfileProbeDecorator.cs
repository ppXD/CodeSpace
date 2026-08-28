using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Messages.Dtos.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CodeSpace.Core.Services.Workflows.Artifacts.Runtime;

/// <summary>
/// Keeps what a probe saw.
///
/// <para>The probe itself observes and answers; it holds no database and cannot persist. Without this, a probe result
/// is an HTTP response and nothing else — an operator who sees a red answer leaves no trace of having seen it, and the
/// next page load looks identical to a healthy one. A decorator rather than a dependency inside the prober, so the
/// qualification boundary stays a pure observer and the recording is separately testable.</para>
///
/// <para>A failed recording never fails the probe. The answer the caller got is still true, and the row it could not
/// write is self-healing: the next probe overwrites it. This is the one swallow in the storage plane that needs no
/// capture gap, because nothing downstream reads the missing row as a fact — a stale row is visibly stale by its own
/// <c>ObservedAt</c>.</para>
/// </summary>
public sealed class RecordingStorageProfileProbeDecorator : IStorageProfileProbeService
{
    private readonly IStorageProfileProbeService _inner;
    private readonly CodeSpaceDbContext _db;
    private readonly TimeProvider _clock;
    private readonly ILogger<RecordingStorageProfileProbeDecorator> _logger;

    public RecordingStorageProfileProbeDecorator(IStorageProfileProbeService inner, CodeSpaceDbContext db, TimeProvider clock, ILogger<RecordingStorageProfileProbeDecorator> logger)
    {
        _inner = inner;
        _db = db;
        _clock = clock;
        _logger = logger;
    }

    public async Task<StorageProfileProbeResult> ProbeAsync(StorageProfileProbeRequest request, CancellationToken cancellationToken)
    {
        var result = await _inner.ProbeAsync(request, cancellationToken).ConfigureAwait(false);

        await RecordAsync(request, result).ConfigureAwait(false);

        return result;
    }

    /// <summary>
    /// Upserts the one health row for this profile.
    ///
    /// <para>A probe that could not resolve a revision records nothing: <c>profile_revision</c> is NOT NULL because a
    /// health row that cannot say WHICH destination it describes cannot be compared against the profile's current
    /// revision, and a reader would have no way to tell a fresh observation from one about a destination the profile
    /// has since left.</para>
    ///
    /// <para>Runs on <see cref="CancellationToken.None"/>: the observation is most worth keeping exactly when the
    /// caller is walking away, and a cancelled probe's Cancelled status is itself a fact about the destination.</para>
    /// </summary>
    private async Task RecordAsync(StorageProfileProbeRequest request, StorageProfileProbeResult result)
    {
        if (result.ProfileRevision is not { } revision) return;

        try
        {
            var existing = await _db.StorageProfileHealth
                .SingleOrDefaultAsync(row => row.TeamId == request.TeamId && row.StorageProfileId == result.ProfileId, CancellationToken.None)
                .ConfigureAwait(false)
                ?? Track(new StorageProfileHealth { TeamId = request.TeamId, StorageProfileId = result.ProfileId });

            existing.ProfileRevision = revision;
            existing.Status = result.Status;
            existing.WriteVerified = result.WriteAccessRequested && result.Status == StorageProfileProbeStatusValue.Available;
            existing.FailureStage = result.Failure?.Stage;
            existing.FailureCode = result.Failure?.Code;
            existing.LatencyMs = result.LatencyMilliseconds;
            existing.ObservedAt = _clock.GetUtcNow();

            await _db.SaveChangesAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Probe of storage profile {ProfileId} answered {Status} but the observation could not be recorded; the stored health stays as it was and the next probe overwrites it", result.ProfileId, result.Status);
        }
    }

    private StorageProfileHealth Track(StorageProfileHealth health)
    {
        _db.StorageProfileHealth.Add(health);
        return health;
    }
}
