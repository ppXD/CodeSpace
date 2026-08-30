using System.Data.Common;
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
///
/// <para>Every row is read and settled on a connection of its OWN, which is what makes a pass survive a row it cannot
/// write. Postgres aborts the whole transaction block on a constraint violation and refuses every statement after it
/// until that block ends, so one shared connection would turn a single refused row into ninety-nine unasked ones — and
/// this pass is dispatched as a command, inside the transaction <c>TransactionalBehavior</c> opens, so the poison
/// would reach the caller's block too and discard the rows that HAD been settled. Taking a connection per row costs a
/// hundred short-lived contexts an hour and buys the only property that matters here: one row nobody could write
/// costs exactly one row.</para>
/// </summary>
public sealed class ArtifactLocationVerifier : IArtifactLocationVerifier
{
    private readonly DbContextOptions<CodeSpaceDbContext> _dbOptions;
    private readonly IStorageRuntimeDriverBroker _broker;
    private readonly TimeProvider _clock;
    private readonly ILogger<ArtifactLocationVerifier> _logger;

    public ArtifactLocationVerifier(DbContextOptions<CodeSpaceDbContext> dbOptions, IStorageRuntimeDriverBroker broker, TimeProvider clock, ILogger<ArtifactLocationVerifier> logger)
    {
        _dbOptions = dbOptions;
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
        var unrecorded = 0;

        foreach (var location in due)
        {
            cancellationToken.ThrowIfCancellationRequested();

            switch (await VerifyOneAsync(location, cancellationToken).ConfigureAwait(false))
            {
                case Verdict.Confirmed: confirmed++; break;
                case Verdict.Restored: restored++; break;
                case Verdict.Missing: missing++; break;
                case Verdict.Corrupt: corrupt++; break;
                case Verdict.Unrecorded: unrecorded++; break;
                default: inconclusive++; break;
            }
        }

        // One line an operator can read the shape of the failure off: a couple of rows racing a neighbouring pass and
        // every row in the batch failing look identical row by row, and only differ in this ratio.
        if (unrecorded > 0) _logger.LogWarning("A verification pass could not record what it observed of {Unrecorded} of the {Checked} locations it examined", unrecorded, due.Count);

        return new ArtifactLocationVerificationSummary
        {
            Checked = due.Count, Confirmed = confirmed, Restored = restored, Missing = missing, Corrupt = corrupt,
            Inconclusive = inconclusive, Unrecorded = unrecorded,
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
        await using var db = CreateDb();

        var recoveryShare = Math.Max(1, batchSize / 4);
        var missing = await OldestAsync(db, ArtifactLocationState.Missing, recoveryShare, cancellationToken).ConfigureAwait(false);
        var available = await OldestAsync(db, ArtifactLocationState.Available, batchSize - missing.Count, cancellationToken).ConfigureAwait(false);

        return [.. available, .. missing];
    }

    private static async Task<List<ArtifactLocation>> OldestAsync(CodeSpaceDbContext db, ArtifactLocationState state, int take, CancellationToken cancellationToken) =>
        take <= 0 ? [] : await db.ArtifactLocation.AsNoTracking()
            .Where(location => location.State == state)
            .OrderBy(location => location.VerifiedAt).ThenBy(location => location.Id)
            .Take(take)
            .ToListAsync(cancellationToken).ConfigureAwait(false);

    private async Task<Verdict> VerifyOneAsync(ArtifactLocation location, CancellationToken cancellationToken)
    {
        var revision = await RevisionAsync(location, cancellationToken).ConfigureAwait(false);

        if (revision == null) return Verdict.Inconclusive;

        try
        {
            // Read eligibility, never Write: verifying must work against a Disabled or Retired profile, which is
            // precisely where bytes sit longest and rot most quietly.
            var resolution = await _broker.OpenAsync(new StorageRuntimeDriverRequest(location.TeamId, revision.StorageProfileId, revision.Revision, Profiles.StorageProfileEligibility.Read), cancellationToken).ConfigureAwait(false);
            if (resolution is not StorageRuntimeDriverResolution.Ready ready) return Verdict.Inconclusive;

            ArtifactStorageHeadResult head;
            var destinationLive = false;

            await using (ready.Lease)
            {
                head = await ready.Lease.Driver.HeadAsync(new ArtifactStorageHeadRequest(location.ObjectKey), cancellationToken).ConfigureAwait(false);

                if (IsObjectMissing(head)) destinationLive = await DestinationAnswersAsync(ready.Lease.Driver, cancellationToken).ConfigureAwait(false);
            }

            // Inside the guard even though the settle carries one of its own: that one answers for the database
            // refusing the write, and this one covers every other way settling a row can fail. Either escaping here
            // would end the batch and take every row behind this one with it.
            return await SettleAsync(location, head, destinationLive, cancellationToken).ConfigureAwait(false);
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
    }

    /// <summary>
    /// The profile revision this location was written under, on a connection of its own so nothing stays open across
    /// the provider round trip that follows — or null, when this pass could not find out.
    ///
    /// <para>Guarded HERE rather than by the caller's try, because this read runs BEFORE that try opens and is
    /// therefore the one way out of a row's work the row's own guard cannot close. It is a database call like every
    /// other one, failing for the ordinary reasons: a pool exhausted for a moment, a database that blinked. Unguarded,
    /// one such moment on one row ends the entire pass and takes every row behind it — the exact loss this containment
    /// exists to prevent, surviving one line above the guard that prevents it.</para>
    ///
    /// <para>A refusal here and a revision that is simply not there are the same answer to the caller, and that answer
    /// is <c>Inconclusive</c>. Never <c>Unrecorded</c>: nothing about the object had been observed at this point, so
    /// nothing failed to be written down, and saying otherwise would claim an observation this pass never made.</para>
    /// </summary>
    private async Task<StorageProfileRevision?> RevisionAsync(ArtifactLocation location, CancellationToken cancellationToken)
    {
        try
        {
            await using var db = CreateDb();

            return await db.StorageProfileRevision.AsNoTracking()
                .SingleOrDefaultAsync(value => value.TeamId == location.TeamId && value.Id == location.StorageProfileRevisionId, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Verification of artifact location {LocationId} could not read the storage profile revision it was written under; the row and its verified_at are left as they were", location.Id);

            return null;
        }
    }

    /// <summary>
    /// Makes this row's observation durable, all of it or none of it, on a connection nothing else is using — and
    /// answers for the attempt when the database will not take it.
    ///
    /// <para>The transaction is EXPLICIT because a restore is two advances of the row — the confirmation, then the
    /// return to <c>Available</c> — and they can be neither merged nor left independent. They cannot be merged: the
    /// schema's <c>artifact_location_event_guard</c> admits an entry only at the location's current or immediately
    /// next revision, so a single save that moved the row two revisions and appended both entries has the first one
    /// rejected, which is a restore that never happens and a warning per row. They cannot be independent either: a
    /// second write the database refuses would leave the first standing, and the row this pass reports as unrecorded
    /// would nonetheless have had its <c>verified_at</c> — the sweep's own cursor — moved. So: two saves, one
    /// transaction. An outcome that recorded nothing leaves nothing recorded.</para>
    ///
    /// <para>The row is <c>Attach</c>ed rather than re-read: it carries the <c>xmin</c> it was read with, and that is
    /// what makes the write conditional on the row still being the one this pass observed. Re-reading it here would
    /// quietly drop that guard and let a pass overwrite a verdict another writer reached in the meantime.</para>
    ///
    /// <para>The guard belongs HERE and not around the whole row's work, because this is the only scope in which the
    /// two failures are distinguishable. <c>Inconclusive</c> says the DESTINATION could not answer; <c>Unrecorded</c>
    /// says WE could not write down an answer we already had. Everything past the first line of this method runs on an
    /// answer that WILL be written down, so a database failure anywhere inside it is the second — while a guard placed
    /// further out would meet the very same exception types coming from the profile and credential reads the broker
    /// does on its way to opening a driver, and would file those as failures to record something nothing had yet
    /// observed.</para>
    ///
    /// <para>Which is also why that first line comes first, before a context or a transaction exists. A row whose
    /// answer was never about the object writes nothing — so opening one to write nothing can only ever produce a
    /// failure this guard would then file as <c>Unrecorded</c>, reporting that an observation was lost on a row where
    /// none was made. Deciding before writing is what keeps both verdicts meaning exactly what they say.</para>
    /// </summary>
    private async Task<Verdict> SettleAsync(ArtifactLocation location, ArtifactStorageHeadResult head, bool destinationLive, CancellationToken cancellationToken)
    {
        if (!IsAboutTheObject(head, destinationLive)) return Verdict.Inconclusive;

        var previous = location.State;

        try
        {
            await using var db = CreateDb();
            db.ArtifactLocation.Attach(location);

            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

            var verdict = await RecordAsync(db, location, head, cancellationToken).ConfigureAwait(false);

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

            Announce(location, previous, verdict);

            return verdict;
        }
        catch (Exception exception) when (exception is DbUpdateException or DbException)
        {
            // BOTH families, and both as BASE types. They are disjoint — DbUpdateException does not derive from
            // DbException — and they arrive from different halves of this method: EF raises the first out of
            // SaveChangesAsync, while BeginTransactionAsync and CommitAsync hand the provider's own DbException
            // straight through. Naming only the first would leave a refused COMMIT to the caller's generic arm, and a
            // refused COMMIT is routine here rather than exotic, because this schema's DEFERRED constraints are
            // checked at exactly that moment.
            //
            // The base types matter for a second reason: a pass that lost this row to a second writer usually collides
            // on ux_artifact_location_event_revision — the winner already took the revision this settle appends —
            // which arrives as a plain DbUpdateException and not as the concurrency subtype. Check constraints and
            // integrity triggers arrive the same way. So the ONE thing established here is that the observation could
            // not be written down. The log says that and nothing more; naming a cause would be inventing one.
            _logger.LogWarning(exception, "This pass could not record what it observed of artifact location {LocationId}; the row is left exactly as it was", location.Id);

            return Verdict.Unrecorded;
        }
    }

    /// <summary>
    /// Whether the provider said anything about the OBJECT — the only kind of answer that gets written down, and
    /// therefore the question that has to be settled before anything is opened to write one.
    ///
    /// <para>Exactly two answers are about the object. A successful HEAD is one: it carries the size and hash the
    /// recorded identity is compared against. <c>Missing</c> is the other — the one error code that is a statement
    /// about the object rather than about the moment. Every other error — throttled, unauthorized, unavailable, a
    /// transport fault — says something about the REQUEST, and a location must never be touched for that.</para>
    ///
    /// <para>Even <c>Missing</c> is only believed once the destination has proved it can still answer, because a
    /// provider cannot tell a deleted object apart from a whole namespace it can no longer see: the local driver
    /// resolves a missing object with <c>File.Exists</c>, which is equally false when the mount underneath is gone.
    /// Without that corroboration an unmounted volume would demote every location a team owns in a single pass.</para>
    /// </summary>
    private static bool IsAboutTheObject(ArtifactStorageHeadResult head, bool destinationLive) => head.IsSuccess || (IsObjectMissing(head) && destinationLive);

    /// <summary>
    /// Turns one provider answer about the object into the durable advances it justifies, one revision at a time,
    /// inside the transaction the caller owns. Only ever reached for an answer <see cref="IsAboutTheObject"/> admitted,
    /// so every path out of here has written something.
    ///
    /// <para>A size or hash that disagrees with what was recorded means the key now holds something else, which is not
    /// the artifact whatever it is. This is a HEAD, not a re-hash: silent bit rot inside an object of the right size is
    /// caught by the read path, which verifies the content digest and raises <c>IntegrityFailure</c>. The division is
    /// deliberate — reads prove content, and this proves presence, which is the half no reader can discover until
    /// someone happens to ask.</para>
    /// </summary>
    private async Task<Verdict> RecordAsync(CodeSpaceDbContext db, ArtifactLocation location, ArtifactStorageHeadResult head, CancellationToken cancellationToken)
    {
        if (!head.IsSuccess)
        {
            if (location.State == ArtifactLocationState.Missing)
            {
                await MarkObservedAsync(db, location, cancellationToken).ConfigureAwait(false);

                return Verdict.Missing;
            }

            await DemoteAsync(db, location, ArtifactLocationState.Missing, "location-object-missing", $"The destination reports no object at {location.ObjectKey}.", cancellationToken).ConfigureAwait(false);

            return Verdict.Missing;
        }

        var metadata = head.Metadata!;
        if (Disagrees(location, metadata, out var detail))
        {
            await DemoteAsync(db, location, ArtifactLocationState.Corrupt, "location-object-mismatch", detail, cancellationToken).ConfigureAwait(false);
            return Verdict.Corrupt;
        }

        await ConfirmAsync(db, location, cancellationToken).ConfigureAwait(false);
        if (location.State == ArtifactLocationState.Missing)
        {
            await RestoreAsync(db, location, cancellationToken).ConfigureAwait(false);

            return Verdict.Restored;
        }

        return Verdict.Confirmed;
    }

    /// <summary>Says out loud only what is already durable: this runs after the save, so a write the database refused announces nothing.</summary>
    private void Announce(ArtifactLocation location, ArtifactLocationState previous, Verdict verdict)
    {
        if (verdict == Verdict.Restored)
        {
            _logger.LogInformation("Artifact location {LocationId} answered again at its destination and returned to Available", location.Id);
            return;
        }

        if (location.State == previous) return;

        _logger.LogWarning("Artifact location {LocationId} is no longer serving the object that was recorded ({State}): {Detail}", location.Id, location.State, location.LastErrorMessage);
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
    private async Task DemoteAsync(CodeSpaceDbContext db, ArtifactLocation location, ArtifactLocationState state, string errorCode, string detail, CancellationToken cancellationToken)
    {
        var now = _clock.GetUtcNow();
        location.State = state;
        location.Revision++;
        location.VerifiedAt = now;
        location.LastErrorCode = errorCode;
        location.LastErrorMessage = detail;
        location.LastModifiedDate = now;
        location.LastModifiedBy = Messages.Constants.SystemUsers.SeederId;
        db.ArtifactLocationEvent.Add(Snapshot(location, ArtifactLocationEventType.StateChanged, now));

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Moves verified_at forward and nothing else. The row was already Available; what changes is only WHEN that was last actually known.</summary>
    /// <summary>
    /// Returns a location the destination can serve again to <c>Available</c>, and clears the error it was carrying.
    ///
    /// <para>Only ever reached after a successful HEAD whose size and ETag agree with what was recorded at write time,
    /// so this restores on the same evidence the original placement was accepted on — never on mere reachability.</para>
    /// </summary>
    private async Task RestoreAsync(CodeSpaceDbContext db, ArtifactLocation location, CancellationToken cancellationToken)
    {
        var now = _clock.GetUtcNow();
        location.State = ArtifactLocationState.Available;
        location.LastErrorCode = null;
        location.LastErrorMessage = null;
        location.Revision++;
        location.VerifiedAt = now;
        location.LastModifiedDate = now;
        location.LastModifiedBy = Messages.Constants.SystemUsers.SeederId;
        db.ArtifactLocationEvent.Add(Snapshot(location, ArtifactLocationEventType.Verified, now));

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
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
    private async Task MarkObservedAsync(CodeSpaceDbContext db, ArtifactLocation location, CancellationToken cancellationToken)
    {
        var now = _clock.GetUtcNow();
        location.Revision++;
        location.VerifiedAt = now;
        location.LastModifiedDate = now;
        location.LastModifiedBy = Messages.Constants.SystemUsers.SeederId;
        db.ArtifactLocationEvent.Add(Snapshot(location, ArtifactLocationEventType.Observed, now));

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task ConfirmAsync(CodeSpaceDbContext db, ArtifactLocation location, CancellationToken cancellationToken)
    {
        var now = _clock.GetUtcNow();
        location.Revision++;
        location.VerifiedAt = now;
        location.LastModifiedDate = now;
        location.LastModifiedBy = Messages.Constants.SystemUsers.SeederId;
        db.ArtifactLocationEvent.Add(Snapshot(location, ArtifactLocationEventType.Verified, now));

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
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

    private CodeSpaceDbContext CreateDb() => new(_dbOptions);

    private enum Verdict { Confirmed, Restored, Missing, Corrupt, Inconclusive, Unrecorded }
}
