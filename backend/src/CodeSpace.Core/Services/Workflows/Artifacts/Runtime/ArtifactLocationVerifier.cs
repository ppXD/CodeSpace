using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Workflows.Artifacts.Providers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CodeSpace.Core.Services.Workflows.Artifacts.Runtime;

/// <summary>
/// One HEAD per location, and a demotion only on an answer that leaves no room for doubt.
///
/// <para>The batch is the least recently verified Available locations, so a deployment converges on re-checking
/// everything without a schedule anyone has to maintain. Ordering by <c>verified_at</c> also means a destination that
/// keeps answering inconclusively is retried first next pass, because nothing moved its timestamp.</para>
/// </summary>
public sealed class ArtifactLocationVerifier : IArtifactLocationVerifier
{
    private readonly CodeSpaceDbContext _db;
    private readonly IStorageRuntimeDriverBroker _broker;
    private readonly TimeProvider _clock;
    private readonly ILogger<ArtifactLocationVerifier> _logger;

    public ArtifactLocationVerifier(CodeSpaceDbContext db, IStorageRuntimeDriverBroker broker, TimeProvider clock, ILogger<ArtifactLocationVerifier> logger)
    {
        _db = db;
        _broker = broker;
        _clock = clock;
        _logger = logger;
    }

    public async Task<ArtifactLocationVerificationSummary> VerifyStaleAsync(int batchSize, CancellationToken cancellationToken)
    {
        var due = await StaleAsync(Math.Clamp(batchSize, 1, 500), cancellationToken).ConfigureAwait(false);
        var confirmed = 0;
        var restored = 0;
        var missing = 0;
        var corrupt = 0;
        var inconclusive = 0;

        foreach (var location in due)
        {
            cancellationToken.ThrowIfCancellationRequested();

            switch (await VerifyOneAsync(location, cancellationToken).ConfigureAwait(false))
            {
                case Verdict.Confirmed: confirmed++; break;
                case Verdict.Restored: restored++; break;
                case Verdict.Missing: missing++; break;
                case Verdict.Corrupt: corrupt++; break;
                default: inconclusive++; break;
            }
        }

        return new ArtifactLocationVerificationSummary
        {
            Checked = due.Count, Confirmed = confirmed, Restored = restored, Missing = missing, Corrupt = corrupt, Inconclusive = inconclusive,
        };
    }

    /// <summary>
    /// The least recently verified locations that a destination can still speak for.
    ///
    /// <para>Deliberately not filtered by team or profile state: a retired profile still serves its own bytes, so its
    /// locations are exactly as worth verifying. <c>Missing</c> is swept alongside <c>Available</c> because demotion
    /// must not be a one-way door — a destination-wide fault that this verifier mistook for per-object loss has to be
    /// able to correct itself once the destination answers again. <c>Corrupt</c> is NOT swept back: it takes a positive
    /// disagreement to reach, which an outage cannot fabricate, so re-reading it would only risk flapping.</para>
    ///
    /// <para>The two populations get separate shares of the batch rather than competing in one ORDER BY. They grow
    /// independently — an abandoned destination leaves thousands of permanently <c>Missing</c> rows behind, and they
    /// are the OLDEST rows in the table by construction — so a single ordering would spend the entire budget
    /// re-asking about bytes already known to be gone while healthy placements went unchecked. Detection is the
    /// primary job; recovering a wrong demotion is the secondary one, and it gets the smaller share.</para>
    /// </summary>
    private async Task<IReadOnlyList<ArtifactLocation>> StaleAsync(int batchSize, CancellationToken cancellationToken)
    {
        var recoveryShare = Math.Max(1, batchSize / 4);
        var missing = await OldestAsync(ArtifactLocationState.Missing, recoveryShare, cancellationToken).ConfigureAwait(false);
        var available = await OldestAsync(ArtifactLocationState.Available, batchSize - missing.Count, cancellationToken).ConfigureAwait(false);

        return [.. available, .. missing];
    }

    private async Task<List<ArtifactLocation>> OldestAsync(ArtifactLocationState state, int take, CancellationToken cancellationToken) =>
        take <= 0 ? [] : await _db.ArtifactLocation
            .Where(location => location.State == state)
            .OrderBy(location => location.VerifiedAt).ThenBy(location => location.Id)
            .Take(take)
            .ToListAsync(cancellationToken).ConfigureAwait(false);

    private async Task<Verdict> VerifyOneAsync(ArtifactLocation location, CancellationToken cancellationToken)
    {
        var revision = await _db.StorageProfileRevision.AsNoTracking()
            .SingleOrDefaultAsync(value => value.TeamId == location.TeamId && value.Id == location.StorageProfileRevisionId, cancellationToken)
            .ConfigureAwait(false);

        if (revision == null) return Verdict.Inconclusive;

        ArtifactStorageHeadResult head;
        var destinationLive = false;
        try
        {
            // Read eligibility, never Write: verifying must work against a Disabled or Retired profile, which is
            // precisely where bytes sit longest and rot most quietly.
            var resolution = await _broker.OpenAsync(new StorageRuntimeDriverRequest(location.TeamId, revision.StorageProfileId, revision.Revision, Profiles.StorageProfileEligibility.Read), cancellationToken).ConfigureAwait(false);
            if (resolution is not StorageRuntimeDriverResolution.Ready ready) return Verdict.Inconclusive;

            await using (ready.Lease)
            {
                head = await ready.Lease.Driver.HeadAsync(new ArtifactStorageHeadRequest(location.ObjectKey), cancellationToken).ConfigureAwait(false);

                if (IsObjectMissing(head)) destinationLive = await DestinationAnswersAsync(ready.Lease.Driver, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Verification of artifact location {LocationId} could not reach its destination; the row and its verified_at are left as they were", location.Id);
            return Verdict.Inconclusive;
        }

        return await SettleAsync(location, head, destinationLive, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Turns one provider answer into a durable observation.
    ///
    /// <para>Only two answers demote. <c>Missing</c> from the provider is the object not being there — the one error
    /// code that is a statement about the object rather than about the moment. A size or ETag that disagrees with what
    /// was recorded means the key now holds something else, which is not the artifact whatever it is. This is a HEAD,
    /// not a re-hash: silent bit rot inside an object of the right size is caught by the read path, which verifies the
    /// content digest and raises <c>IntegrityFailure</c>. The division is deliberate — reads prove content, and this
    /// proves presence, which is the half no reader can discover until someone happens to ask. Every other
    /// error — throttled, unauthorized, unavailable, a transport fault — says something about the request, and a
    /// location must never be demoted for that.</para>
    ///
    /// <para>Even <c>Missing</c> is only believed once the destination has proved it can still answer, because a
    /// provider cannot tell a deleted object apart from a whole namespace it can no longer see: the local driver
    /// resolves a missing object with <c>File.Exists</c>, which is equally false when the mount underneath is gone.
    /// Without that corroboration an unmounted volume would demote every location a team owns in a single pass.</para>
    /// </summary>
    private async Task<Verdict> SettleAsync(ArtifactLocation location, ArtifactStorageHeadResult head, bool destinationLive, CancellationToken cancellationToken)
    {
        if (!head.IsSuccess)
        {
            if (!IsObjectMissing(head) || !destinationLive) return Verdict.Inconclusive;

            if (location.State == ArtifactLocationState.Missing)
            {
                await MarkObservedAsync(location, cancellationToken).ConfigureAwait(false);

                return Verdict.Missing;
            }

            await DemoteAsync(location, ArtifactLocationState.Missing, "location-object-missing",
                $"The destination reports no object at {location.ObjectKey}.", cancellationToken).ConfigureAwait(false);

            return Verdict.Missing;
        }

        var metadata = head.Metadata!;
        if (Disagrees(location, metadata, out var detail))
        {
            await DemoteAsync(location, ArtifactLocationState.Corrupt, "location-object-mismatch", detail, cancellationToken).ConfigureAwait(false);
            return Verdict.Corrupt;
        }

        await ConfirmAsync(location, cancellationToken).ConfigureAwait(false);
        if (location.State == ArtifactLocationState.Missing)
        {
            await RestoreAsync(location, cancellationToken).ConfigureAwait(false);

            return Verdict.Restored;
        }

        return Verdict.Confirmed;
    }

    /// <summary>
    /// Whether the object at the key is still the one that was recorded. Size is compared always; the ETag only when
    /// BOTH sides have one, because a provider that stopped reporting one has told us nothing, and treating that as a
    /// mismatch would demote a healthy object.
    /// </summary>
    /// <summary>The checksum recorded at write time, as the lowercase hex the drivers report, or null when the provider gave none.</summary>
    private static string? RecordedSha256(ArtifactLocation location) =>
        location.ProviderChecksum is { Length: > 0 } checksum && string.Equals(location.ProviderChecksumAlgorithm, "Sha256", StringComparison.OrdinalIgnoreCase)
            ? Convert.ToHexStringLower(checksum)
            : null;

    private static bool IsObjectMissing(ArtifactStorageHeadResult head) => !head.IsSuccess && head.Error?.Code == ArtifactStorageErrorCode.Missing;

    /// <summary>Asks whether the destination itself is still answering, so a gone namespace cannot be read as a gone object.</summary>
    private static async Task<bool> DestinationAnswersAsync(IArtifactStorageDriver driver, CancellationToken cancellationToken)
    {
        var probe = await driver.ProbeAsync(new ArtifactStorageProbeRequest(), cancellationToken).ConfigureAwait(false);

        return probe.Status is ArtifactStorageProbeStatus.Available or ArtifactStorageProbeStatus.ReadOnly;
    }

    /// <summary>
    /// Whether the destination is demonstrably holding something other than the recorded object.
    ///
    /// <para>Only content-derived identity counts. Size always is. A provider-computed hash is, when the provider
    /// returns one. <c>ProviderETag</c> deliberately is NOT compared: an ETag is provider-defined, and the local
    /// driver derives it from the file's mtime — so a restore from backup, a re-upload, or anything that rewrites
    /// identical bytes changes it. Treating that as evidence would mark a whole destination Corrupt the first time it
    /// was recovered, which is the opposite of what a verifier is for.</para>
    /// </summary>
    private static bool Disagrees(ArtifactLocation location, ArtifactStorageObjectMetadata metadata, out string detail)
    {
        if (location.ObservedSizeBytes is { } size && metadata.Length != size)
        {
            detail = $"The destination holds {metadata.Length} bytes at {location.ObjectKey}; {size} were recorded.";
            return true;
        }

        if (RecordedSha256(location) is { } recorded && metadata.Sha256 is { } observed && !string.Equals(recorded, observed, StringComparison.OrdinalIgnoreCase))
        {
            detail = $"The destination hashes the object at {location.ObjectKey} to {observed}; {recorded} was recorded.";
            return true;
        }

        detail = string.Empty;
        return false;
    }

    /// <summary>Advances the row and its append-only event together: the schema requires a byte-identical event snapshot at every revision, and a demotion that could not record itself would be a state nothing explains.</summary>
    private async Task DemoteAsync(ArtifactLocation location, ArtifactLocationState state, string errorCode, string detail, CancellationToken cancellationToken)
    {
        var now = _clock.GetUtcNow();
        location.State = state;
        location.Revision++;
        location.VerifiedAt = now;
        location.LastErrorCode = errorCode;
        location.LastErrorMessage = detail;
        location.LastModifiedDate = now;
        location.LastModifiedBy = Messages.Constants.SystemUsers.SeederId;
        _db.ArtifactLocationEvent.Add(Snapshot(location, ArtifactLocationEventType.StateChanged, now));

        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        _logger.LogWarning("Artifact location {LocationId} is no longer serving the object that was recorded ({State}): {Detail}", location.Id, state, detail);
    }

    /// <summary>Moves verified_at forward and nothing else. The row was already Available; what changes is only WHEN that was last actually known.</summary>
    /// <summary>
    /// Returns a location the destination can serve again to <c>Available</c>, and clears the error it was carrying.
    ///
    /// <para>Only ever reached after a successful HEAD whose size and ETag agree with what was recorded at write time,
    /// so this restores on the same evidence the original placement was accepted on — never on mere reachability.</para>
    /// </summary>
    private async Task RestoreAsync(ArtifactLocation location, CancellationToken cancellationToken)
    {
        var now = _clock.GetUtcNow();
        location.State = ArtifactLocationState.Available;
        location.LastErrorCode = null;
        location.LastErrorMessage = null;
        location.Revision++;
        location.VerifiedAt = now;
        location.LastModifiedDate = now;
        location.LastModifiedBy = Messages.Constants.SystemUsers.SeederId;
        _db.ArtifactLocationEvent.Add(Snapshot(location, ArtifactLocationEventType.Verified, now));

        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        _logger.LogInformation("Artifact location {LocationId} answered again at its destination and returned to Available", location.Id);
    }

    /// <summary>
    /// Records that a location the sweep already knows is <c>Missing</c> was asked again and answered the same way.
    ///
    /// <para>The state does not change, but <c>verified_at</c> does, and that is the entire point: it is also the
    /// sweep's cursor, so a conclusive answer that did not move it would leave the row pinned at the front of the
    /// ordering forever. Enough such rows and the batch is permanently full of them and no healthy placement is ever
    /// examined again. A demotion and a re-confirmation are both answers about the object; only an outcome the
    /// destination could not answer leaves the column alone.</para>
    ///
    /// <para>It is an <c>Observed</c> ledger entry rather than <c>Verified</c>: nothing was verified to be present.
    /// The row's revision advances because the schema requires every observation of a location to be an entry — which
    /// is also why the recovery share of the batch is small, since this writes one event per re-check.</para>
    /// </summary>
    private async Task MarkObservedAsync(ArtifactLocation location, CancellationToken cancellationToken)
    {
        var now = _clock.GetUtcNow();
        location.Revision++;
        location.VerifiedAt = now;
        location.LastModifiedDate = now;
        location.LastModifiedBy = Messages.Constants.SystemUsers.SeederId;
        _db.ArtifactLocationEvent.Add(Snapshot(location, ArtifactLocationEventType.Observed, now));

        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task ConfirmAsync(ArtifactLocation location, CancellationToken cancellationToken)
    {
        var now = _clock.GetUtcNow();
        location.Revision++;
        location.VerifiedAt = now;
        location.LastModifiedDate = now;
        location.LastModifiedBy = Messages.Constants.SystemUsers.SeederId;
        _db.ArtifactLocationEvent.Add(Snapshot(location, ArtifactLocationEventType.Verified, now));

        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    private static ArtifactLocationEvent Snapshot(ArtifactLocation location, ArtifactLocationEventType eventType, DateTimeOffset now) => new()
    {
        Id = Guid.NewGuid(), TeamId = location.TeamId, ArtifactLocationId = location.Id, Revision = location.Revision,
        EventType = eventType, State = location.State, ObservedAt = now,
        ProviderObjectVersion = location.ProviderObjectVersion, ProviderETag = location.ProviderETag,
        ProviderChecksumAlgorithm = location.ProviderChecksumAlgorithm, ProviderChecksum = location.ProviderChecksum,
        ObservedSizeBytes = location.ObservedSizeBytes, VerifiedAt = location.VerifiedAt,
        ContentEncoding = location.ContentEncoding, EncryptionKeyVersion = location.EncryptionKeyVersion,
        ErrorCode = location.LastErrorCode, ErrorMessage = location.LastErrorMessage, DetailsJson = "{}",
        CreatedBy = Messages.Constants.SystemUsers.SeederId,
    };

    private enum Verdict { Confirmed, Restored, Missing, Corrupt, Inconclusive }
}
