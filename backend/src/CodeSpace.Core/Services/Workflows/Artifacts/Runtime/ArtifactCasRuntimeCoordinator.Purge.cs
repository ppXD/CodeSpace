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
            ArtifactCasPurgeClaimResult.Claimed claimed => await DeleteAsync(claimed.Claim, cancellationToken).ConfigureAwait(false),
            ArtifactCasPurgeClaimResult.Purged purged => new ArtifactCasPurgeResult.Purged(purged.LocationId, purged.LocationRevision, true),
            ArtifactCasPurgeClaimResult.Rejected rejected => new ArtifactCasPurgeResult.Rejected(rejected.Problem),
            _ => new ArtifactCasPurgeResult.Rejected(Problem(ArtifactCasProblemCode.ProviderFailure)),
        };
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
        if (locations.Count != 1) return ClaimRejected(ArtifactCasProblemCode.MultipleLocationsUnsupported);
        var location = locations[0];
        if (location.State == ArtifactLocationState.Purged)
            return new ArtifactCasPurgeClaimResult.Purged(location.Id, location.Revision);
        if (location.State is not (ArtifactLocationState.Available or ArtifactLocationState.Deleting))
            return ClaimRejected(ArtifactCasProblemCode.LocationUnavailable);
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
                ExpectedETag = claim.ProviderETag, ExpectedVersion = claim.ProviderObjectVersion,
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
        location.State = ArtifactLocationState.Available;
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
