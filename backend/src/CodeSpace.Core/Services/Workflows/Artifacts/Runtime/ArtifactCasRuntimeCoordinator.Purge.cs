using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Workflows.Artifacts.Profiles;
using CodeSpace.Core.Services.Workflows.Artifacts.Providers;
using Microsoft.EntityFrameworkCore;

namespace CodeSpace.Core.Services.Workflows.Artifacts.Runtime;

public sealed partial class ArtifactCasRuntimeCoordinator
{
    public async Task<ArtifactCasPurgeResult> PurgeAsync(ArtifactCasPurgeRequest request, CancellationToken cancellationToken)
    {
        var timeout = Validate(request);
        var claimed = await ClaimPurgeAsync(request, cancellationToken).ConfigureAwait(false);
        if (claimed.Problem != null) return new ArtifactCasPurgeResult.Rejected(claimed.Problem);
        if (claimed.Location!.State == ArtifactLocationState.Purged)
            return new ArtifactCasPurgeResult.Purged(claimed.Location.Id, claimed.Location.Revision, true);

        var activation = await OpenDriverAsync(new DriverActivationRequest(request.TeamId, claimed.Location.ProfileId,
            claimed.Location.ProfileRevision, StorageProfileEligibility.Read, timeout, StorageProviderCapabilities.Delete), cancellationToken).ConfigureAwait(false);
        if (activation.Problem != null) return new ArtifactCasPurgeResult.Rejected(activation.Problem);

        StorageRuntimeDriverLease? lease = activation.Lease!;
        try
        {
            var deletion = await InvokeAsync(token => lease.Driver.DeleteAsync(new ArtifactStorageDeleteRequest(claimed.Location.ObjectKey)
            {
                ExpectedETag = claimed.Location.ProviderETag,
                ExpectedVersion = claimed.Location.ProviderObjectVersion,
            }, token), timeout, cancellationToken, lease).ConfigureAwait(false);
            if (deletion.Problem != null) return new ArtifactCasPurgeResult.Rejected(deletion.Problem);
            if (deletion.Timeout) return new ArtifactCasPurgeResult.Rejected(Problem(ArtifactCasProblemCode.ProviderTimeout, true));
            if (deletion.Value?.Error is { Code: not ArtifactStorageErrorCode.Missing } error)
                return new ArtifactCasPurgeResult.Rejected(Map(error, readMissing: true));

            return await FinalizePurgeAsync(request, claimed.Location, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (lease != null) await DisposeLeaseQuietlyAsync(lease).ConfigureAwait(false);
        }
    }

    private async Task<PurgeClaim> ClaimPurgeAsync(ArtifactCasPurgeRequest request, CancellationToken cancellationToken)
    {
        await using var db = CreateDb();
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var locations = await db.ArtifactLocation.FromSqlInterpolated($"""
            SELECT artifact_location.*, artifact_location.xmin
            FROM artifact_location
            WHERE team_id = {request.TeamId} AND artifact_object_id = {request.ArtifactObjectId}
            ORDER BY id
            FOR UPDATE
            """).ToListAsync(cancellationToken).ConfigureAwait(false);

        if (locations.Count == 0) return Reject(ArtifactCasProblemCode.ArtifactMissing);
        if (locations.Count != 1) return Reject(ArtifactCasProblemCode.MultipleLocationsUnsupported);
        var location = locations[0];
        if (location.State is not (ArtifactLocationState.Available or ArtifactLocationState.Deleting or ArtifactLocationState.Purged))
            return Reject(ArtifactCasProblemCode.LocationUnavailable);
        var profile = await db.StorageProfileRevision.AsNoTracking()
            .Where(value => value.TeamId == request.TeamId && value.Id == location.StorageProfileRevisionId)
            .Select(value => new { value.StorageProfileId, value.Revision })
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (profile == null) return Reject(ArtifactCasProblemCode.ProfileRevisionMissing);
        if (location.State == ArtifactLocationState.Available)
        {
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
        }

        return new PurgeClaim(new PurgeLocation
        {
            Id = location.Id, ProfileId = profile.StorageProfileId, ProfileRevision = profile.Revision,
            ObjectKey = location.ObjectKey, ProviderETag = location.ProviderETag,
            ProviderObjectVersion = location.ProviderObjectVersion, State = location.State, Revision = location.Revision,
        }, null);
    }

    private async Task<ArtifactCasPurgeResult> FinalizePurgeAsync(ArtifactCasPurgeRequest request, PurgeLocation claimed, CancellationToken cancellationToken)
    {
        await using var db = CreateDb();
        var location = await db.ArtifactLocation.SingleOrDefaultAsync(value => value.TeamId == request.TeamId
            && value.Id == claimed.Id && value.ArtifactObjectId == request.ArtifactObjectId, cancellationToken).ConfigureAwait(false);
        if (location == null) return new ArtifactCasPurgeResult.Rejected(Problem(ArtifactCasProblemCode.ArtifactMissing));
        if (location.State == ArtifactLocationState.Purged)
            return new ArtifactCasPurgeResult.Purged(location.Id, location.Revision, true);
        if (location.State != ArtifactLocationState.Deleting || location.Revision != claimed.Revision)
            return new ArtifactCasPurgeResult.Rejected(Problem(ArtifactCasProblemCode.StaleWorker, true));

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
        location.LastModifiedBy = request.ActorId;
        db.ArtifactLocationEvent.Add(PurgeEvent(location, request.ActorId, now));
        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return new ArtifactCasPurgeResult.Purged(location.Id, location.Revision, false);
        }
        catch (DbUpdateConcurrencyException)
        {
            return new ArtifactCasPurgeResult.Rejected(Problem(ArtifactCasProblemCode.StaleWorker, true));
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

    private static PurgeClaim Reject(ArtifactCasProblemCode code) => new(null, Problem(code));

    private sealed record PurgeClaim(PurgeLocation? Location, ArtifactCasProblem? Problem);
    private sealed record PurgeLocation
    {
        public required Guid Id { get; init; }
        public required Guid ProfileId { get; init; }
        public required int ProfileRevision { get; init; }
        public required string ObjectKey { get; init; }
        public required string? ProviderETag { get; init; }
        public required string? ProviderObjectVersion { get; init; }
        public required ArtifactLocationState State { get; init; }
        public required long Revision { get; init; }
    }
}
