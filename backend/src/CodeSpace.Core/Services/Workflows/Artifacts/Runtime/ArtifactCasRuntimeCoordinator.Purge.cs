using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Workflows.Artifacts.Profiles;
using CodeSpace.Core.Services.Workflows.Artifacts.Providers;
using Microsoft.EntityFrameworkCore;

namespace CodeSpace.Core.Services.Workflows.Artifacts.Runtime;

public sealed partial class ArtifactCasRuntimeCoordinator
{
    public async Task<ArtifactCasPurgeResult> PurgeAsync(ArtifactCasPurgeRequest request, CancellationToken cancellationToken)
    {
        var claim = await ClaimAsync(request, cancellationToken).ConfigureAwait(false);
        return claim switch
        {
            // Corrupt is claimable so it can be abandoned, but this entry point exists to delete bytes. Handing the
            // claim on would strand the row in Deleting when the delete refused it, which is worse than the refusal.
            ArtifactCasPurgeClaimResult.Claimed corrupt when corrupt.Claim.ClaimedFrom == ArtifactLocationState.Corrupt
                => await ReleaseAndRejectAsync(corrupt.Claim, cancellationToken).ConfigureAwait(false),
            ArtifactCasPurgeClaimResult.Claimed claimed => await DeleteAsync(claimed.Claim, cancellationToken).ConfigureAwait(false),
            ArtifactCasPurgeClaimResult.Purged purged => new ArtifactCasPurgeResult.Purged(purged.LocationId, purged.LocationRevision, true),
            ArtifactCasPurgeClaimResult.Rejected rejected => new ArtifactCasPurgeResult.Rejected(rejected.Problem),
            _ => new ArtifactCasPurgeResult.Rejected(Problem(ArtifactCasProblemCode.ProviderFailure)),
        };
    }

    private async Task<ArtifactCasPurgeResult> ReleaseAndRejectAsync(ArtifactCasPurgeClaim claim, CancellationToken cancellationToken)
    {
        await ReleaseAsync(claim, cancellationToken).ConfigureAwait(false);

        return new ArtifactCasPurgeResult.Rejected(Problem(ArtifactCasProblemCode.LocationUnavailable));
    }

    public async Task<ArtifactCasPurgeClaimResult> ClaimAsync(ArtifactCasPurgeRequest request, CancellationToken cancellationToken)
    {
        var timeout = Validate(request);
        await using var db = CreateDb();
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var locations = await db.ArtifactLocation.FromSqlInterpolated($"""
            SELECT artifact_location.*, artifact_location.xmin
            FROM artifact_location
            WHERE team_id = {request.TeamId} AND artifact_object_id = {request.ArtifactObjectId}
            ORDER BY id
            FOR UPDATE
            """).ToListAsync(cancellationToken).ConfigureAwait(false);

        if (locations.Count == 0) return ClaimRejected(ArtifactCasProblemCode.ArtifactMissing);

        // The FOR UPDATE above stays object-wide on purpose — it is the mutual exclusion two concurrent drains of the
        // same object need. Only the SELECTION narrows: a caller draining one destination says which row, and a
        // caller of a single-placed object still says nothing.
        var location = request.ArtifactLocationId is { } named
            ? locations.SingleOrDefault(value => value.Id == named)
            : locations.Count == 1 ? locations[0] : null;

        if (location == null) return ClaimRejected(ArtifactCasProblemCode.ArtifactMissing);
        if (location.State == ArtifactLocationState.Purged)
            return new ArtifactCasPurgeClaimResult.Purged(location.Id, location.Revision);
        // Missing joins Available and Deleting: the destination has said the object is not there, and letting the row
        // reach Deleting is the only way it can reach Purged — which is what spends the idempotency generation and
        // makes that content writable under this revision again. Refusing it left the row unreachable by every path
        // at once: unreadable, undrainable, and blocking its profile's retirement forever.
        //
        // Corrupt is deliberately NOT admitted. It asserts the destination holds something that is NOT this object,
        // and the delete cannot always be conditioned — a provider without a stable ETag (the local driver, since the
        // recorded one is derived from a modification time) would delete whatever is at that key. A record positively
        // identified as naming someone else's bytes is closed by abandoning the record, never by deleting them.
        // Corrupt is claimable but not deletable: claiming is about taking the row, deleting is about touching bytes.
        // Without a claim it could never reach Deleting, therefore never Purged, therefore never release its profile
        // or let that content be written under this revision again — a record with no exit at all.
        if (location.State is not (ArtifactLocationState.Available or ArtifactLocationState.Deleting
            or ArtifactLocationState.Missing or ArtifactLocationState.Corrupt))
            return ClaimRejected(ArtifactCasProblemCode.LocationUnavailable);

        var claimedFrom = location.State;
        var profile = await db.StorageProfileRevision.AsNoTracking()
            .Where(value => value.TeamId == request.TeamId && value.Id == location.StorageProfileRevisionId)
            .Select(value => new { value.StorageProfileId, value.Revision })
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (profile == null) return ClaimRejected(ArtifactCasProblemCode.ProfileRevisionMissing);

        var now = await DatabaseClockAsync(db, cancellationToken).ConfigureAwait(false);
        location.State = ArtifactLocationState.Deleting;
        location.Revision++;
        location.LastErrorCode = null;
        location.LastErrorMessage = null;
        location.LastModifiedDate = now;
        location.LastModifiedBy = request.ActorId;
        db.ArtifactLocationEvent.Add(PurgeEvent(location, request.ActorId, now));
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        return new ArtifactCasPurgeClaimResult.Claimed(new ArtifactCasPurgeClaim
        {
            ClaimedFrom = claimedFrom,
            TeamId = request.TeamId, ArtifactObjectId = request.ArtifactObjectId,
            LocationId = location.Id, LocationRevision = location.Revision,
            StorageProfileId = profile.StorageProfileId, StorageProfileRevision = profile.Revision,
            ObjectKey = location.ObjectKey, ProviderETag = location.ProviderETag,
            ProviderObjectVersion = location.ProviderObjectVersion, ActorId = request.ActorId, OperationTimeout = timeout,
        });
    }

    public async Task<ArtifactCasPurgeResult> DeleteAsync(ArtifactCasPurgeClaim claim, CancellationToken cancellationToken)
    {
        Validate(claim);
        if (claim.ClaimedFrom == ArtifactLocationState.Corrupt)
            return new ArtifactCasPurgeResult.Rejected(Problem(ArtifactCasProblemCode.LocationUnavailable));
        if (!await ClaimIsCurrentAsync(claim, cancellationToken).ConfigureAwait(false))
            return new ArtifactCasPurgeResult.Rejected(Problem(ArtifactCasProblemCode.StaleWorker, true));
        var activation = await OpenDriverAsync(new DriverActivationRequest(claim.TeamId, claim.StorageProfileId,
            claim.StorageProfileRevision, StorageProfileEligibility.Read, claim.OperationTimeout, StorageProviderCapabilities.Delete), cancellationToken).ConfigureAwait(false);
        if (activation.Problem != null) return new ArtifactCasPurgeResult.Rejected(activation.Problem);

        StorageRuntimeDriverLease? lease = activation.Lease!;
        try
        {
            var deletion = await InvokeAsync(token => lease.Driver.DeleteAsync(new ArtifactStorageDeleteRequest(claim.ObjectKey)
            {
                ExpectedETag = DurableETag(claim.ProviderETag, lease.Driver.Capabilities), ExpectedVersion = claim.ProviderObjectVersion,
            }, token), claim.OperationTimeout, cancellationToken, lease).ConfigureAwait(false);
            if (deletion.Problem != null) return new ArtifactCasPurgeResult.Rejected(deletion.Problem, EffectMayHaveOccurred: true);
            if (deletion.Timeout) return new ArtifactCasPurgeResult.Rejected(Problem(ArtifactCasProblemCode.ProviderTimeout, true), EffectMayHaveOccurred: true);
            if (deletion.Value?.Error is { Code: not ArtifactStorageErrorCode.Missing } error)
                return new ArtifactCasPurgeResult.Rejected(Map(error, readMissing: true));

            return await FinalizePurgeAsync(claim, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (lease != null) await DisposeLeaseQuietlyAsync(lease).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Settles a claimed placement as <c>Purged</c> without deleting anything, once a live HEAD proves the
    /// destination cannot serve the object.
    ///
    /// <para>Proof is taken here, under this claim, with Read eligibility so a Disabled or Retired profile can still
    /// be asked. It is never inherited from a stored health row: that describes a destination at some past moment and
    /// against a different eligibility, and the one thing this must never do is close the record of bytes somebody
    /// can still read.</para>
    ///
    /// <para>Every answer that is ABOUT the destination or the credential — the object is not there, the bucket is
    /// not there, the key is refused — is grounds to close the record. An answer that serves the object is not, and
    /// releases the claim instead. An answer that is merely a bad moment is neither, and leaves the claim standing
    /// for a caller to retry or release.</para>
    /// </summary>
    public async Task<ArtifactCasAbandonResult> AbandonAsync(ArtifactCasPurgeClaim claim, CancellationToken cancellationToken)
    {
        Validate(claim);
        if (!await ClaimIsCurrentAsync(claim, cancellationToken).ConfigureAwait(false))
            return new ArtifactCasAbandonResult.Rejected(Problem(ArtifactCasProblemCode.StaleWorker, true));

        var activation = await OpenDriverAsync(new DriverActivationRequest(claim.TeamId, claim.StorageProfileId,
            claim.StorageProfileRevision, StorageProfileEligibility.Read, claim.OperationTimeout, StorageProviderCapabilities.None), cancellationToken).ConfigureAwait(false);

        // A destination that cannot be opened is evidence only when the refusal is DURABLE — a revoked credential, a
        // profile revision whose config no longer resolves. A retryable failure (a broker timeout, a resolution blip)
        // is a statement about the moment, and closing the record on it would settle Purged over bytes one bad second
        // could not testify about. The broker's mapping already draws this line: transient reasons carry IsRetryable.
        if (activation.Problem is { } refusal && !Settles(refusal)) return new ArtifactCasAbandonResult.Rejected(refusal);
        if (activation.Problem != null) return await FinalizeAbandonAsync(claim, $"the destination could not be opened ({activation.Problem.Code})", cancellationToken).ConfigureAwait(false);

        StorageRuntimeDriverLease? lease = activation.Lease!;
        try
        {
            var head = await InvokeAsync(token => lease.Driver.HeadAsync(new ArtifactStorageHeadRequest(claim.ObjectKey), token),
                claim.OperationTimeout, cancellationToken, lease).ConfigureAwait(false);

            if (head.Problem != null) return new ArtifactCasAbandonResult.Rejected(head.Problem);
            if (head.Timeout) return new ArtifactCasAbandonResult.Rejected(Problem(ArtifactCasProblemCode.ProviderTimeout, true));

            if (head.Value?.Error is { } error)
                return Settles(error)
                    ? await FinalizeAbandonAsync(claim, $"the destination answered '{error.Code}' for {claim.ObjectKey}", cancellationToken).ConfigureAwait(false)
                    : new ArtifactCasAbandonResult.Rejected(Map(error, readMissing: false));

            // A successful HEAD proves something is AT the key, not that the key holds THIS object. For a placement
            // already recorded Corrupt that distinction is the whole question: the destination is healthy and serving
            // something, and treating presence as service released the claim, while the delete path refuses Corrupt
            // outright — leaving the record with no exit at all and its profile permanently un-retirable.
            if (await ServesSomethingElseAsync(claim, head.Value!.Metadata!, cancellationToken).ConfigureAwait(false))
                return await FinalizeAbandonAsync(claim, $"the destination holds something other than this object at {claim.ObjectKey}", cancellationToken).ConfigureAwait(false);

            await ReleaseAsync(claim, cancellationToken).ConfigureAwait(false);

            return new ArtifactCasAbandonResult.StillServed(claim.LocationId, $"the destination served {claim.ObjectKey}");
        }
        finally
        {
            if (lease != null) await DisposeLeaseQuietlyAsync(lease).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Which provider answers say something durable about the destination rather than about this moment.
    ///
    /// <para><c>Missing</c>, <c>Unauthorized</c> and <c>Forbidden</c> are statements about the object or the
    /// credential. <c>Unavailable</c> is two different answers wearing one code — a deleted bucket AND a transient
    /// 5xx or network fault both classify to it — and retryability is what tells them apart: the classifier marks a
    /// gone namespace non-retryable because retrying does not bring a deleted bucket back. A retryable Unavailable is
    /// a bad moment, and a bad moment must never close the record of bytes it could not testify about.</para>
    /// </summary>
    /// <summary>
    /// Whether the destination is demonstrably holding something OTHER than this object.
    ///
    /// <para>Compared against the CAS object's own identity — its size and digest — rather than against the
    /// placement row, because the row is what is under suspicion. Only content-derived evidence counts: size always
    /// is, and a provider-computed hash is when the provider returns one. <c>ProviderETag</c> is deliberately not
    /// compared; it is provider-defined and a local destination derives it from a modification time, so a restore or
    /// a migration would read as a different object.</para>
    ///
    /// <para>Absence of proof is not proof: a provider that returns no hash and a matching size yields false, and
    /// the claim is released. Closing a record needs a positive disagreement.</para>
    /// </summary>
    private async Task<bool> ServesSomethingElseAsync(ArtifactCasPurgeClaim claim, ArtifactStorageObjectMetadata metadata, CancellationToken cancellationToken)
    {
        await using var db = CreateDb();
        var identity = await db.ArtifactObject.AsNoTracking()
            .Where(value => value.TeamId == claim.TeamId && value.Id == claim.ArtifactObjectId)
            .Select(value => new { value.SizeBytes, value.Digest })
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);

        if (identity == null) return false;
        if (metadata.Length != identity.SizeBytes) return true;

        return metadata.Sha256 is { } observed && !string.Equals(observed, Convert.ToHexStringLower(identity.Digest), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Whether a failure to even OPEN the destination is durable evidence. The broker's mapping already draws the
    /// line — a revoked credential or a vanished profile revision is non-retryable, a broker timeout or resolution
    /// blip is retryable — so retryability IS the moment-versus-destination distinction at this seam.
    /// </summary>
    internal static bool Settles(ArtifactCasProblem problem) => !problem.IsRetryable;

    internal static bool Settles(ArtifactStorageError error) => error.Code
        is ArtifactStorageErrorCode.Missing
        or ArtifactStorageErrorCode.Unauthorized
        or ArtifactStorageErrorCode.Forbidden
        || (error.Code == ArtifactStorageErrorCode.Unavailable && !error.IsRetryable);

    private async Task<ArtifactCasAbandonResult> FinalizeAbandonAsync(ArtifactCasPurgeClaim claim, string evidence, CancellationToken cancellationToken)
    {
        var finalized = await FinalizePurgeAsync(claim, cancellationToken).ConfigureAwait(false);

        return finalized switch
        {
            ArtifactCasPurgeResult.Purged purged => new ArtifactCasAbandonResult.Abandoned(purged.LocationId, purged.LocationRevision, evidence),
            ArtifactCasPurgeResult.Rejected rejected => new ArtifactCasAbandonResult.Rejected(rejected.Problem),
            _ => new ArtifactCasAbandonResult.Rejected(Problem(ArtifactCasProblemCode.ProviderFailure)),
        };
    }

    public async Task<bool> ReleaseAsync(ArtifactCasPurgeClaim claim, CancellationToken cancellationToken)
    {
        Validate(claim);
        await using var db = CreateDb();
        var location = await db.ArtifactLocation.SingleOrDefaultAsync(value => value.TeamId == claim.TeamId
            && value.Id == claim.LocationId && value.ArtifactObjectId == claim.ArtifactObjectId
            && value.ObjectKey == claim.ObjectKey && value.ProviderETag == claim.ProviderETag
            && value.ProviderObjectVersion == claim.ProviderObjectVersion, cancellationToken).ConfigureAwait(false);
        if (location == null || location.State != ArtifactLocationState.Deleting || location.Revision != claim.LocationRevision) return false;

        var now = await DatabaseClockAsync(db, cancellationToken).ConfigureAwait(false);
        // Back where the claim found it, not to Available. A row claimed from Missing or Corrupt was not good before
        // and releasing the claim establishes nothing about it — declaring it good here would put unreadable bytes
        // back in front of every reader on the strength of a claim that did nothing.
        location.State = claim.ClaimedFrom;
        location.Revision++;
        location.LastErrorCode = null;
        location.LastErrorMessage = null;
        location.LastModifiedDate = now;
        location.LastModifiedBy = claim.ActorId;
        db.ArtifactLocationEvent.Add(PurgeEvent(location, claim.ActorId, now));
        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (DbUpdateConcurrencyException) { return false; }
    }

    private async Task<bool> ClaimIsCurrentAsync(ArtifactCasPurgeClaim claim, CancellationToken cancellationToken)
    {
        await using var db = CreateDb();
        var profileRevisionId = await db.StorageProfileRevision.AsNoTracking()
            .Where(value => value.TeamId == claim.TeamId && value.StorageProfileId == claim.StorageProfileId
                && value.Revision == claim.StorageProfileRevision)
            .Select(value => (Guid?)value.Id).SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        return profileRevisionId != null && await db.ArtifactLocation.AsNoTracking().AnyAsync(value => value.TeamId == claim.TeamId
            && value.Id == claim.LocationId && value.ArtifactObjectId == claim.ArtifactObjectId
            && value.StorageProfileRevisionId == profileRevisionId && value.ObjectKey == claim.ObjectKey
            && value.ProviderETag == claim.ProviderETag && value.ProviderObjectVersion == claim.ProviderObjectVersion
            && value.State == ArtifactLocationState.Deleting && value.Revision == claim.LocationRevision, cancellationToken).ConfigureAwait(false);
    }

    private async Task<ArtifactCasPurgeResult> FinalizePurgeAsync(ArtifactCasPurgeClaim claim, CancellationToken cancellationToken)
    {
        await using var db = CreateDb();
        var location = await db.ArtifactLocation.SingleOrDefaultAsync(value => value.TeamId == claim.TeamId
            && value.Id == claim.LocationId && value.ArtifactObjectId == claim.ArtifactObjectId
            && value.ObjectKey == claim.ObjectKey && value.ProviderETag == claim.ProviderETag
            && value.ProviderObjectVersion == claim.ProviderObjectVersion, cancellationToken).ConfigureAwait(false);
        if (location == null) return new ArtifactCasPurgeResult.Rejected(Problem(ArtifactCasProblemCode.ArtifactMissing), EffectMayHaveOccurred: true);
        if (location.State == ArtifactLocationState.Purged)
            return new ArtifactCasPurgeResult.Purged(location.Id, location.Revision, true);
        if (location.State != ArtifactLocationState.Deleting || location.Revision != claim.LocationRevision)
            return new ArtifactCasPurgeResult.Rejected(Problem(ArtifactCasProblemCode.StaleWorker, true), EffectMayHaveOccurred: true);

        var now = await DatabaseClockAsync(db, cancellationToken).ConfigureAwait(false);
        location.State = ArtifactLocationState.Purged;
        location.Revision++;
        location.ProviderObjectVersion = null;
        location.ProviderETag = null;
        location.ProviderChecksumAlgorithm = null;
        location.ProviderChecksum = null;
        location.ObservedSizeBytes = null;
        location.VerifiedAt = null;
        location.LastErrorCode = null;
        location.LastErrorMessage = null;
        location.LastModifiedDate = now;
        location.LastModifiedBy = claim.ActorId;
        db.ArtifactLocationEvent.Add(PurgeEvent(location, claim.ActorId, now));
        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return new ArtifactCasPurgeResult.Purged(location.Id, location.Revision, false);
        }
        catch (DbUpdateConcurrencyException)
        {
            return new ArtifactCasPurgeResult.Rejected(Problem(ArtifactCasProblemCode.StaleWorker, true), EffectMayHaveOccurred: true);
        }
    }

    private static ArtifactLocationEvent PurgeEvent(ArtifactLocation location, Guid actorId, DateTimeOffset now) => new()
    {
        Id = Guid.NewGuid(), TeamId = location.TeamId, ArtifactLocationId = location.Id, Revision = location.Revision,
        EventType = ArtifactLocationEventType.StateChanged, State = location.State, ObservedAt = now,
        ProviderObjectVersion = location.ProviderObjectVersion, ProviderETag = location.ProviderETag,
        ProviderChecksumAlgorithm = location.ProviderChecksumAlgorithm, ProviderChecksum = location.ProviderChecksum,
        ObservedSizeBytes = location.ObservedSizeBytes, VerifiedAt = location.VerifiedAt,
        ContentEncoding = location.ContentEncoding, EncryptionKeyVersion = location.EncryptionKeyVersion,
        ErrorCode = location.LastErrorCode, ErrorMessage = location.LastErrorMessage, DetailsJson = "{}", CreatedBy = actorId,
    };

    private static TimeSpan Validate(ArtifactCasPurgeRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.TeamId == Guid.Empty || request.ArtifactObjectId == Guid.Empty || request.ActorId == Guid.Empty)
            throw new ArgumentException("Team, artifact object and actor ids are required.", nameof(request));
        return ValidateTimeout(request.OperationTimeout);
    }

    private static void Validate(ArtifactCasPurgeClaim claim)
    {
        ArgumentNullException.ThrowIfNull(claim);
        if (claim.TeamId == Guid.Empty || claim.ArtifactObjectId == Guid.Empty || claim.LocationId == Guid.Empty
            || claim.LocationRevision <= 0 || claim.StorageProfileId == Guid.Empty || claim.StorageProfileRevision <= 0
            || string.IsNullOrWhiteSpace(claim.ObjectKey) || claim.ActorId == Guid.Empty)
            throw new ArgumentException("A purge claim requires exact team, object, location, revision, profile, key and actor coordinates.", nameof(claim));
        ValidateTimeout(claim.OperationTimeout);
    }

    private static ArtifactCasPurgeClaimResult ClaimRejected(ArtifactCasProblemCode code) => new ArtifactCasPurgeClaimResult.Rejected(Problem(code));
}
