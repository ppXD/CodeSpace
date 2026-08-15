using System.Security.Cryptography;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Workflows.Artifacts.Providers;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace CodeSpace.Core.Services.Workflows.Artifacts.Runtime;

/// <summary>
/// Profile-pinned streaming transfer/read coordinator for the additive CAS v2 tables. Provider I/O is deliberately
/// outside database transactions; durable intent + monotonic revision/fence claims make every commit replay-safe.
/// </summary>
public sealed class ArtifactCasRuntimeCoordinator : IArtifactCasRuntimeCoordinator
{
    private static readonly TimeSpan DefaultOperationTimeout = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan MaximumOperationTimeout = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan MinimumLeaseMargin = TimeSpan.FromMilliseconds(250);
    private const int HashBufferSize = 128 * 1024;

    private readonly DbContextOptions<CodeSpaceDbContext> _dbOptions;
    private readonly IStorageRuntimeDriverBroker _driverBroker;
    private readonly TimeProvider _clock;

    public ArtifactCasRuntimeCoordinator(DbContextOptions<CodeSpaceDbContext> dbOptions, IStorageRuntimeDriverBroker driverBroker, TimeProvider clock)
    {
        _dbOptions = dbOptions;
        _driverBroker = driverBroker;
        _clock = clock;
    }

    public async Task<ArtifactCasTransferResult> PutAsync(ArtifactCasTransferRequest request, CancellationToken cancellationToken)
    {
        var input = Validate(request);
        // Caller-supplied lineage is identity, not authority. Until an authoritative active-attempt adapter exists,
        // accepting it would let a replaced/zombie attempt mint a fresh effect intent.
        if (request.ExecutionIdentity != null)
            return new ArtifactCasTransferResult.Rejected(null, Problem(ArtifactCasProblemCode.ExecutionAdmissionUnavailable));
        var resolved = await ResolveProfileRevisionAsync(request.TeamId, request.StorageProfileId, request.StorageProfileRevision, cancellationToken).ConfigureAwait(false);
        if (resolved.Problem != null) return new ArtifactCasTransferResult.Rejected(null, resolved.Problem);

        var intent = await EnsureIntentAsync(request, resolved.ProfileRevisionId!.Value, input.Digest, cancellationToken).ConfigureAwait(false);
        if (intent.Problem != null) return new ArtifactCasTransferResult.Rejected(intent.Id, intent.Problem);
        if (intent.State == ArtifactTransferState.Committed)
            return new ArtifactCasTransferResult.Committed(intent.Id, intent.ArtifactObjectId!.Value, intent.ArtifactLocationId!.Value, true);
        if (intent.State is ArtifactTransferState.Failed or ArtifactTransferState.Cancelled)
            return new ArtifactCasTransferResult.Rejected(intent.Id, StoredProblem(intent));
        if (intent.State == ArtifactTransferState.RetryScheduled && intent.NextAttemptAt > _clock.GetUtcNow())
            return new ArtifactCasTransferResult.Deferred(intent.Id, intent.NextAttemptAt!.Value, StoredProblem(intent));

        var claimed = await ClaimAsync(request.TeamId, intent.Id, request.ActorId, input.Timeout, cancellationToken).ConfigureAwait(false);
        var claim = claimed.Intent;
        if (claim.State == ArtifactTransferState.Committed)
            return new ArtifactCasTransferResult.Committed(claim.Id, claim.ArtifactObjectId!.Value, claim.ArtifactLocationId!.Value, true);
        if (claim.State is ArtifactTransferState.Failed or ArtifactTransferState.Cancelled)
            return new ArtifactCasTransferResult.Rejected(claim.Id, StoredProblem(claim));
        if (claim.State == ArtifactTransferState.RetryScheduled && claim.NextAttemptAt > _clock.GetUtcNow())
            return new ArtifactCasTransferResult.Deferred(claim.Id, claim.NextAttemptAt!.Value, StoredProblem(claim));
        if (!claimed.Acquired)
            return new ArtifactCasTransferResult.Deferred(claim.Id, claim.LeaseExpiresAt ?? _clock.GetUtcNow(), Problem(ArtifactCasProblemCode.TransferInProgress, true));

        StorageRuntimeDriverLease? driverLease = null;
        try
        {
            if (claim.State == ArtifactTransferState.RetryScheduled)
            {
                claim = await TransitionAsync(claim, ArtifactTransferState.Uploading, request.ActorId, cancellationToken).ConfigureAwait(false);
                if (claim.IsStale) return Stale(intent.Id);
            }
            var create = await OpenDriverAsync(new DriverActivationRequest(request.TeamId, request.StorageProfileId, request.StorageProfileRevision, input.Timeout, StorageProviderCapabilities.StreamingWrite | StorageProviderCapabilities.StreamingRead | StorageProviderCapabilities.ConditionalCreate), cancellationToken).ConfigureAwait(false);
            if (create.Problem != null) return await HandleProblemAsync(claim, request.ActorId, create.Problem, cancellationToken).ConfigureAwait(false);
            driverLease = create.Lease!;
            return await DriveTransferAsync(request, input, claim, driverLease, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (driverLease != null) await DisposeLeaseQuietlyAsync(driverLease).ConfigureAwait(false);
        }
    }

    public async Task<ArtifactCasReadResult> OpenReadAsync(ArtifactCasReadRequest request, CancellationToken cancellationToken)
    {
        var timeout = Validate(request);
        var resolved = await ResolveProfileRevisionAsync(request.TeamId, request.StorageProfileId, request.StorageProfileRevision, cancellationToken).ConfigureAwait(false);
        if (resolved.Problem != null) return new ArtifactCasReadResult.Unavailable(resolved.Problem);

        ReadLocation? stored;
        await using (var db = CreateDb())
        {
            stored = await (from location in db.ArtifactLocation.AsNoTracking()
                            join artifact in db.ArtifactObject.AsNoTracking()
                                on new { location.TeamId, Id = location.ArtifactObjectId } equals new { artifact.TeamId, artifact.Id }
                            where location.TeamId == request.TeamId && location.ArtifactObjectId == request.ArtifactObjectId
                                && location.StorageProfileRevisionId == resolved.ProfileRevisionId && location.State == ArtifactLocationState.Available
                            orderby location.VerifiedAt descending, location.Id
                            select new ReadLocation(location.ObjectKey, location.ProviderETag, location.ProviderObjectVersion, artifact.SizeBytes, artifact.Digest))
                .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        }

        if (stored == null) return new ArtifactCasReadResult.Unavailable(Problem(ArtifactCasProblemCode.ArtifactMissing));
        var create = await OpenDriverAsync(new DriverActivationRequest(request.TeamId, request.StorageProfileId, request.StorageProfileRevision, timeout, StorageProviderCapabilities.StreamingRead), cancellationToken).ConfigureAwait(false);
        if (create.Problem != null) return new ArtifactCasReadResult.Unavailable(create.Problem);

        StorageRuntimeDriverLease? driverLease = create.Lease!;
        try
        {
            var driver = driverLease.Driver;
            var head = await InvokeAsync(token => driver.HeadAsync(new ArtifactStorageHeadRequest(stored.ObjectKey), token), timeout, cancellationToken, driverLease).ConfigureAwait(false);
            if (head.Problem != null) return new ArtifactCasReadResult.Unavailable(head.Problem);
            if (head.Timeout) return new ArtifactCasReadResult.Unavailable(Problem(ArtifactCasProblemCode.ProviderTimeout, true));
            if (head.Value?.Error != null) return new ArtifactCasReadResult.Unavailable(Map(head.Value.Error, readMissing: true));
            if (!MetadataMatches(stored, head.Value!.Metadata!))
                return new ArtifactCasReadResult.Unavailable(Problem(ArtifactCasProblemCode.TargetCorrupt));

            var opened = await InvokeAsync(token => driver.OpenReadAsync(new ArtifactStorageReadRequest(stored.ObjectKey)
            {
                ExpectedETag = stored.ProviderETag,
                ExpectedVersion = stored.ProviderObjectVersion,
            }, token), timeout, cancellationToken, driverLease).ConfigureAwait(false);
            if (opened.Problem != null) return new ArtifactCasReadResult.Unavailable(opened.Problem);
            if (opened.Timeout) return new ArtifactCasReadResult.Unavailable(Problem(ArtifactCasProblemCode.ProviderTimeout, true));
            if (opened.Value?.Error != null) return new ArtifactCasReadResult.Unavailable(Map(opened.Value.Error, readMissing: true));
            if (opened.Value!.ContentLength != stored.Size || opened.Value.TotalLength != stored.Size
                || !MetadataAgrees(head.Value.Metadata!, opened.Value.Metadata!, stored.ObjectKey))
            {
                await opened.Value.Content!.DisposeAsync().ConfigureAwait(false);
                return new ArtifactCasReadResult.Unavailable(Problem(ArtifactCasProblemCode.TargetCorrupt));
            }

            var stream = new ArtifactCasVerifyingReadStream(opened.Value.Content!, driverLease, stored.Size, stored.Digest);
            driverLease = null;
            return new ArtifactCasReadResult.Opened(stream, stored.Size, Convert.ToHexStringLower(stored.Digest));
        }
        finally
        {
            if (driverLease != null) await DisposeLeaseQuietlyAsync(driverLease).ConfigureAwait(false);
        }
    }

    private async Task<ArtifactCasTransferResult> DriveTransferAsync(ArtifactCasTransferRequest request, ValidTransfer input, IntentSnapshot claim, StorageRuntimeDriverLease driverLease, CancellationToken cancellationToken)
    {
        var driver = driverLease.Driver;
        var current = claim;
        if (current.State is ArtifactTransferState.Intended or ArtifactTransferState.RetryScheduled)
        {
            current = await TransitionAsync(current, ArtifactTransferState.Uploading, request.ActorId, cancellationToken).ConfigureAwait(false);
            if (current.IsStale) return Stale(claim.Id);
        }

        if (current.State == ArtifactTransferState.Uploading)
        {
            if (!await RenewLeaseAsync(current, request.ActorId, input.Timeout, cancellationToken).ConfigureAwait(false)) return Stale(claim.Id);
            var head = await InvokeAsync(token => driver.HeadAsync(new ArtifactStorageHeadRequest(request.TargetObjectKey), token), input.Timeout, cancellationToken, driverLease).ConfigureAwait(false);
            if (head.Problem != null) return await HandleProblemAsync(current, request.ActorId, head.Problem, cancellationToken).ConfigureAwait(false);
            if (head.Timeout) return await HandleProblemAsync(current, request.ActorId, Problem(ArtifactCasProblemCode.ProviderTimeout, true), cancellationToken).ConfigureAwait(false);
            if (head.Value!.IsSuccess)
            {
                if (!HeadCanMatch(request.TargetObjectKey, input, head.Value.Metadata!))
                    return await HandleProblemAsync(current, request.ActorId, Problem(ArtifactCasProblemCode.TargetCorrupt), cancellationToken).ConfigureAwait(false);
            }
            else if (head.Value.Error!.Code == ArtifactStorageErrorCode.Missing)
            {
                if (!await RenewLeaseAsync(current, request.ActorId, input.Timeout, cancellationToken).ConfigureAwait(false)) return Stale(claim.Id);
                var put = await InvokeOwnedInputAsync(token => driver.PutAsync(new ArtifactStoragePutRequest(request.TargetObjectKey, request.Content)
                {
                    ContentLength = request.ExpectedSizeBytes,
                    ExpectedSha256 = request.ExpectedSha256,
                    ContentType = request.ContentType,
                    Condition = ArtifactStorageWriteCondition.CreateOnly,
                }, token), input.Timeout, cancellationToken, driverLease).ConfigureAwait(false);
                if (put.Problem != null) return await HandleProblemAsync(current, request.ActorId, put.Problem, cancellationToken).ConfigureAwait(false);
                if (put.Timeout) return await HandleProblemAsync(current, request.ActorId, Problem(ArtifactCasProblemCode.ProviderTimeout, true), cancellationToken).ConfigureAwait(false);
                if (!put.Value!.IsSuccess && put.Value.Error!.Code != ArtifactStorageErrorCode.AlreadyExists)
                    return await HandleProblemAsync(current, request.ActorId, Map(put.Value.Error), cancellationToken).ConfigureAwait(false);
            }
            else
            {
                return await HandleProblemAsync(current, request.ActorId, Map(head.Value.Error), cancellationToken).ConfigureAwait(false);
            }

            current = await TransitionAsync(current, ArtifactTransferState.Uploaded, request.ActorId, cancellationToken).ConfigureAwait(false);
            if (current.IsStale) return Stale(claim.Id);
        }

        if (current.State == ArtifactTransferState.Uploaded)
        {
            current = await TransitionAsync(current, ArtifactTransferState.Verifying, request.ActorId, cancellationToken).ConfigureAwait(false);
            if (current.IsStale) return Stale(claim.Id);
        }

        if (current.State != ArtifactTransferState.Verifying)
            return new ArtifactCasTransferResult.Rejected(current.Id, Problem(ArtifactCasProblemCode.ProviderFailure));

        var verification = await VerifyAsync(driverLease, request.TargetObjectKey, input, new LeaseRenewal(current, request.ActorId), cancellationToken).ConfigureAwait(false);
        if (verification.Problem != null) return await HandleProblemAsync(current, request.ActorId, verification.Problem, cancellationToken).ConfigureAwait(false);
        return await CommitAsync(current, request.ActorId, verification.Metadata!, cancellationToken).ConfigureAwait(false);
    }

    private async Task<Verification> VerifyAsync(StorageRuntimeDriverLease driverLease, string objectKey, ValidTransfer input, LeaseRenewal renewal, CancellationToken cancellationToken)
    {
        var driver = driverLease.Driver;
        if (!await RenewLeaseAsync(renewal.Claim, renewal.ActorId, input.Timeout, cancellationToken).ConfigureAwait(false))
            return new Verification(null, Problem(ArtifactCasProblemCode.StaleWorker, true));
        var head = await InvokeAsync(token => driver.HeadAsync(new ArtifactStorageHeadRequest(objectKey), token), input.Timeout, cancellationToken, driverLease).ConfigureAwait(false);
        if (head.Problem != null) return new Verification(null, head.Problem);
        if (head.Timeout) return new Verification(null, Problem(ArtifactCasProblemCode.ProviderTimeout, true));
        if (head.Value?.Error != null) return new Verification(null, Map(head.Value.Error, readMissing: true));
        if (!HeadCanMatch(objectKey, input, head.Value!.Metadata!)) return new Verification(null, Problem(ArtifactCasProblemCode.TargetCorrupt));

        if (!await RenewLeaseAsync(renewal.Claim, renewal.ActorId, input.Timeout, cancellationToken).ConfigureAwait(false))
            return new Verification(null, Problem(ArtifactCasProblemCode.StaleWorker, true));
        var read = await InvokeAsync(token => driver.OpenReadAsync(new ArtifactStorageReadRequest(objectKey)
        {
            ExpectedETag = head.Value.Metadata!.ETag,
            ExpectedVersion = head.Value.Metadata.Version,
        }, token), input.Timeout, cancellationToken, driverLease).ConfigureAwait(false);
        if (read.Problem != null) return new Verification(null, read.Problem);
        if (read.Timeout) return new Verification(null, Problem(ArtifactCasProblemCode.ProviderTimeout, true));
        if (read.Value?.Error != null) return new Verification(null, Map(read.Value.Error, readMissing: true));

        var content = read.Value!.Content!;
        driverLease.Own(content);
        if (read.Value.ContentLength != input.Size || read.Value.TotalLength != input.Size)
            return new Verification(null, Problem(ArtifactCasProblemCode.TargetCorrupt));
        if (!MetadataAgrees(head.Value.Metadata!, read.Value.Metadata!, objectKey))
            return new Verification(null, Problem(ArtifactCasProblemCode.TargetCorrupt));

        if (!await RenewLeaseAsync(renewal.Claim, renewal.ActorId, input.Timeout, cancellationToken).ConfigureAwait(false))
            return new Verification(null, Problem(ArtifactCasProblemCode.StaleWorker, true));
        var observed = await HashAsync(content, driverLease, input.Timeout, cancellationToken).ConfigureAwait(false);
        if (observed.Problem != null) return new Verification(null, observed.Problem);
        if (observed.Timeout) return new Verification(null, Problem(ArtifactCasProblemCode.ProviderTimeout, true));
        if (observed.Size != input.Size || !CryptographicOperations.FixedTimeEquals(observed.Digest!, input.Digest))
            return new Verification(null, Problem(ArtifactCasProblemCode.TargetCorrupt));
        return new Verification(head.Value.Metadata, null);
    }

    private async Task<ArtifactCasTransferResult> CommitAsync(IntentSnapshot claim, Guid actorId, ArtifactStorageObjectMetadata metadata, CancellationToken cancellationToken)
    {
        const int maximumCommitAttempts = 5;
        for (var attempt = 0; attempt < maximumCommitAttempts; attempt++)
        {
            await using var db = CreateDb();
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var intent = await db.ArtifactTransferIntent.SingleAsync(value => value.TeamId == claim.TeamId && value.Id == claim.Id, cancellationToken).ConfigureAwait(false);
                if (intent.State == ArtifactTransferState.Committed)
                    return new ArtifactCasTransferResult.Committed(intent.Id, intent.ArtifactObjectId!.Value, intent.ArtifactLocationId!.Value, true);
                var now = await DatabaseClockAsync(db, cancellationToken).ConfigureAwait(false);
                if (intent.State != ArtifactTransferState.Verifying || !LeaseIsCurrent(intent, claim.Fence, now))
                    return Stale(claim.Id);

                var artifact = await db.ArtifactObject.SingleOrDefaultAsync(value => value.TeamId == claim.TeamId && value.DigestAlgorithm == ArtifactDigestAlgorithm.Sha256 && value.Digest == claim.Digest, cancellationToken).ConfigureAwait(false);
                if (artifact != null && artifact.SizeBytes != claim.Size)
                    return await RollbackAndRejectAsync(transaction, claim, actorId, ArtifactCasProblemCode.TargetCorrupt, cancellationToken).ConfigureAwait(false);
                if (artifact == null)
                {
                    artifact = new ArtifactObject
                    {
                        Id = Guid.NewGuid(), TeamId = claim.TeamId, DigestAlgorithm = ArtifactDigestAlgorithm.Sha256,
                        Digest = claim.Digest, SizeBytes = claim.Size, CreatedDate = now, CreatedBy = actorId,
                    };
                    db.ArtifactObject.Add(artifact);
                }

                var location = await db.ArtifactLocation.SingleOrDefaultAsync(value => value.TeamId == claim.TeamId && value.StorageProfileRevisionId == claim.ProfileRevisionId && value.ObjectKey == claim.ObjectKey, cancellationToken).ConfigureAwait(false);
                if (location != null && !Reusable(location, artifact, claim))
                    return await RollbackAndRejectAsync(transaction, claim, actorId, ArtifactCasProblemCode.IdempotencyConflict, cancellationToken).ConfigureAwait(false);
                if (location == null)
                {
                    location = new ArtifactLocation
                    {
                        Id = Guid.NewGuid(), TeamId = claim.TeamId, ArtifactObjectId = artifact.Id, StorageProfileRevisionId = claim.ProfileRevisionId,
                        Locator = claim.Locator, ObjectKey = claim.ObjectKey, ProviderObjectVersion = metadata.Version, ProviderETag = metadata.ETag,
                        ProviderChecksumAlgorithm = "Sha256", ProviderChecksum = claim.Digest, ObservedSizeBytes = claim.Size,
                        State = ArtifactLocationState.Available, Revision = 1, VerifiedAt = now,
                        CreatedDate = now, CreatedBy = actorId, LastModifiedDate = now, LastModifiedBy = actorId,
                    };
                    db.ArtifactLocation.Add(location);
                    db.ArtifactLocationEvent.Add(Event(location, actorId));
                }
                else
                {
                    // A successful readback is a new observation even when the CAS bytes/location already exist.
                    // Refresh provider conditions so future reads never pin a superseded ETag/version.
                    location.ProviderObjectVersion = metadata.Version;
                    location.ProviderETag = metadata.ETag;
                    location.ProviderChecksumAlgorithm = "Sha256";
                    location.ProviderChecksum = claim.Digest;
                    location.ObservedSizeBytes = claim.Size;
                    location.State = ArtifactLocationState.Available;
                    location.Revision++;
                    location.VerifiedAt = now;
                    location.LastErrorCode = null;
                    location.LastErrorMessage = null;
                    location.LastModifiedDate = now;
                    location.LastModifiedBy = actorId;
                    db.ArtifactLocationEvent.Add(Event(location, actorId));
                }

                intent.State = ArtifactTransferState.Committed;
                intent.Revision++;
                intent.ArtifactObjectId = artifact.Id;
                intent.ArtifactLocationId = location.Id;
                intent.CompletedAt = now;
                intent.WorkerLeaseExpiresAt = null;
                intent.LastErrorCode = null;
                intent.LastErrorMessage = null;
                intent.NextAttemptAt = null;
                intent.LastModifiedDate = intent.CompletedAt.Value;
                intent.LastModifiedBy = actorId;
                await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return new ArtifactCasTransferResult.Committed(intent.Id, artifact.Id, location.Id, false);
            }
            catch (Exception exception) when (IsUniqueViolation(exception) || exception is DbUpdateConcurrencyException)
            {
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                var raceOutcome = await ReadCommitRaceOutcomeAsync(claim, cancellationToken).ConfigureAwait(false);
                if (raceOutcome != null) return raceOutcome;
                if (attempt == maximumCommitAttempts - 1)
                    return await HandleProblemAsync(claim, actorId, Problem(ArtifactCasProblemCode.ProviderUnavailableTransient, true), cancellationToken).ConfigureAwait(false);
            }
        }

        return Stale(claim.Id);
    }

    /// <summary>
    /// A location xmin collision does not imply this transfer lost ownership: distinct valid intents may reverify the
    /// same CAS target concurrently. Null means the caller still owns a live Verifying intent and should retry.
    /// </summary>
    private async Task<ArtifactCasTransferResult?> ReadCommitRaceOutcomeAsync(IntentSnapshot claim, CancellationToken cancellationToken)
    {
        await using var db = CreateDb();
        var intent = await db.ArtifactTransferIntent.AsNoTracking().SingleAsync(value => value.TeamId == claim.TeamId && value.Id == claim.Id, cancellationToken).ConfigureAwait(false);
        if (intent.State == ArtifactTransferState.Committed)
            return new ArtifactCasTransferResult.Committed(intent.Id, intent.ArtifactObjectId!.Value, intent.ArtifactLocationId!.Value, true);
        var now = await DatabaseClockAsync(db, cancellationToken).ConfigureAwait(false);
        return intent.State == ArtifactTransferState.Verifying && LeaseIsCurrent(intent, claim.Fence, now) ? null : Stale(claim.Id);
    }

    private async Task<ArtifactCasTransferResult> RollbackAndRejectAsync(Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction transaction, IntentSnapshot claim, Guid actorId, ArtifactCasProblemCode code, CancellationToken cancellationToken)
    {
        await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
        return await HandleProblemAsync(claim, actorId, Problem(code), cancellationToken).ConfigureAwait(false);
    }

    private async Task<ArtifactCasTransferResult> HandleProblemAsync(IntentSnapshot claim, Guid actorId, ArtifactCasProblem problem, CancellationToken cancellationToken)
    {
        await using var db = CreateDb();
        var intent = await db.ArtifactTransferIntent.SingleAsync(value => value.TeamId == claim.TeamId && value.Id == claim.Id, cancellationToken).ConfigureAwait(false);
        var leaseNow = await DatabaseClockAsync(db, cancellationToken).ConfigureAwait(false);
        if (!LeaseIsCurrent(intent, claim.Fence, leaseNow)) return Stale(claim.Id);
        var now = _clock.GetUtcNow();
        if (intent.State == ArtifactTransferState.Committed)
            return new ArtifactCasTransferResult.Committed(intent.Id, intent.ArtifactObjectId!.Value, intent.ArtifactLocationId!.Value, true);

        intent.Revision++;
        intent.LastErrorCode = problem.Code.ToString();
        intent.LastErrorMessage = SafeMessage(problem.Code);
        intent.LastModifiedDate = now;
        intent.LastModifiedBy = actorId;
        if (problem.IsRetryable && intent.State is ArtifactTransferState.Intended or ArtifactTransferState.Uploading or ArtifactTransferState.Uploaded or ArtifactTransferState.Verifying)
        {
            intent.State = ArtifactTransferState.RetryScheduled;
            intent.WorkerLeaseExpiresAt = null;
            intent.RetryCount++;
            intent.NextAttemptAt = now + RetryDelay(intent.RetryCount);
        }
        else
        {
            intent.State = ArtifactTransferState.Failed;
            intent.WorkerLeaseExpiresAt = null;
            intent.NextAttemptAt = null;
            intent.CompletedAt = now;
        }

        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateConcurrencyException)
        {
            return Stale(claim.Id);
        }

        return intent.State == ArtifactTransferState.RetryScheduled
            ? new ArtifactCasTransferResult.Deferred(intent.Id, intent.NextAttemptAt!.Value, problem)
            : new ArtifactCasTransferResult.Rejected(intent.Id, problem);
    }

    private async Task<IntentSnapshot> EnsureIntentAsync(ArtifactCasTransferRequest request, Guid profileRevisionId, byte[] digest, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            await using var db = CreateDb();
            var existing = await db.ArtifactTransferIntent.AsNoTracking().SingleOrDefaultAsync(value => value.TeamId == request.TeamId && value.StorageProfileRevisionId == profileRevisionId && value.IdempotencyKey == request.IdempotencyKey, cancellationToken).ConfigureAwait(false);
            if (existing != null) return Snapshot(existing, Matches(existing, request, digest) ? null : Problem(ArtifactCasProblemCode.IdempotencyConflict));

            var now = _clock.GetUtcNow();
            var intent = new ArtifactTransferIntent
            {
                Id = Guid.NewGuid(), TeamId = request.TeamId, StorageProfileRevisionId = profileRevisionId,
                IdempotencyKey = request.IdempotencyKey, ExpectedDigestAlgorithm = ArtifactDigestAlgorithm.Sha256,
                ExpectedDigest = digest, ExpectedSizeBytes = request.ExpectedSizeBytes, TargetLocator = request.TargetObjectKey,
                TargetObjectKey = request.TargetObjectKey, State = ArtifactTransferState.Intended, Revision = 1,
                ExecutionAttemptId = request.ExecutionIdentity?.AttemptId, ExecutionAttemptOrdinal = request.ExecutionIdentity?.AttemptOrdinal,
                ExecutionGeneration = request.ExecutionIdentity?.Generation, RetryCount = 0,
                CreatedDate = now, CreatedBy = request.ActorId, LastModifiedDate = now, LastModifiedBy = request.ActorId,
            };
            db.ArtifactTransferIntent.Add(intent);
            try
            {
                await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                return Snapshot(intent, null);
            }
            catch (DbUpdateException exception) when (IsUniqueViolation(exception))
            {
                // The idempotency winner is re-read in a fresh context; a failed context is never reused.
            }
        }

        await using var finalDb = CreateDb();
        var winner = await finalDb.ArtifactTransferIntent.AsNoTracking().SingleAsync(value => value.TeamId == request.TeamId && value.StorageProfileRevisionId == profileRevisionId && value.IdempotencyKey == request.IdempotencyKey, cancellationToken).ConfigureAwait(false);
        return Snapshot(winner, Matches(winner, request, digest) ? null : Problem(ArtifactCasProblemCode.IdempotencyConflict));
    }

    private async Task<ClaimResult> ClaimAsync(Guid teamId, Guid intentId, Guid actorId, TimeSpan timeout, CancellationToken cancellationToken)
    {
        await using var db = CreateDb();
        var scheduleNow = _clock.GetUtcNow();
        var leaseMilliseconds = (long)Math.Ceiling(LeaseDuration(timeout).TotalMilliseconds);
        var affected = await db.Database.ExecuteSqlInterpolatedAsync($$"""
            UPDATE artifact_transfer_intent
            SET worker_fence_epoch = COALESCE(worker_fence_epoch, 0) + 1,
                worker_lease_expires_at = clock_timestamp() + ({{leaseMilliseconds}} * INTERVAL '1 millisecond'),
                revision = revision + 1,
                last_modified_date = clock_timestamp(),
                last_modified_by = {{actorId}}
            WHERE team_id = {{teamId}} AND id = {{intentId}}
              AND state IN ('Intended', 'Uploading', 'Uploaded', 'Verifying', 'RetryScheduled')
              AND (state <> 'RetryScheduled' OR next_attempt_at <= {{scheduleNow}})
              AND (worker_lease_expires_at IS NULL OR worker_lease_expires_at <= clock_timestamp())
            """, cancellationToken).ConfigureAwait(false);
        var intent = await db.ArtifactTransferIntent.AsNoTracking().SingleAsync(value => value.TeamId == teamId && value.Id == intentId, cancellationToken).ConfigureAwait(false);
        return new ClaimResult(Snapshot(intent, null), affected == 1);
    }

    private async Task<bool> RenewLeaseAsync(IntentSnapshot claim, Guid actorId, TimeSpan timeout, CancellationToken cancellationToken)
    {
        await using var db = CreateDb();
        var leaseMilliseconds = (long)Math.Ceiling(LeaseDuration(timeout).TotalMilliseconds);
        var affected = await db.Database.ExecuteSqlInterpolatedAsync($$"""
            UPDATE artifact_transfer_intent
            SET worker_lease_expires_at = clock_timestamp() + ({{leaseMilliseconds}} * INTERVAL '1 millisecond'),
                revision = revision + 1,
                last_modified_date = clock_timestamp(),
                last_modified_by = {{actorId}}
            WHERE team_id = {{claim.TeamId}} AND id = {{claim.Id}} AND worker_fence_epoch = {{claim.Fence}}
              AND state IN ('Intended', 'Uploading', 'Uploaded', 'Verifying', 'RetryScheduled')
              AND worker_lease_expires_at > clock_timestamp()
              AND worker_lease_expires_at < clock_timestamp() + ({{leaseMilliseconds}} * INTERVAL '1 millisecond')
            """, cancellationToken).ConfigureAwait(false);
        if (affected == 1) return true;
        var intent = await db.ArtifactTransferIntent.AsNoTracking().SingleAsync(value => value.TeamId == claim.TeamId && value.Id == claim.Id, cancellationToken).ConfigureAwait(false);
        var now = await DatabaseClockAsync(db, cancellationToken).ConfigureAwait(false);
        return LeaseIsCurrent(intent, claim.Fence, now) && intent.State is not (ArtifactTransferState.Committed or ArtifactTransferState.Failed or ArtifactTransferState.Cancelled);
    }

    private async Task<IntentSnapshot> TransitionAsync(IntentSnapshot claim, ArtifactTransferState state, Guid actorId, CancellationToken cancellationToken)
    {
        await using var db = CreateDb();
        var intent = await db.ArtifactTransferIntent.SingleAsync(value => value.TeamId == claim.TeamId && value.Id == claim.Id, cancellationToken).ConfigureAwait(false);
        var now = await DatabaseClockAsync(db, cancellationToken).ConfigureAwait(false);
        if (!LeaseIsCurrent(intent, claim.Fence, now)) return claim with { IsStale = true };
        intent.State = state;
        intent.Revision++;
        intent.NextAttemptAt = null;
        intent.LastErrorCode = null;
        intent.LastErrorMessage = null;
        intent.LastModifiedDate = now;
        intent.LastModifiedBy = actorId;
        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return Snapshot(intent, null);
        }
        catch (DbUpdateConcurrencyException)
        {
            return claim with { IsStale = true };
        }
    }

    private async Task<ResolvedProfileRevision> ResolveProfileRevisionAsync(Guid teamId, Guid profileId, int profileRevision, CancellationToken cancellationToken)
    {
        await using var db = CreateDb();
        var row = await (from profile in db.StorageProfile.AsNoTracking()
                         join revision in db.StorageProfileRevision.AsNoTracking().Where(value => value.Revision == profileRevision)
                             on new { profile.TeamId, StorageProfileId = profile.Id } equals new { revision.TeamId, revision.StorageProfileId } into revisions
                         from revision in revisions.DefaultIfEmpty()
                         where profile.TeamId == teamId && profile.Id == profileId
                         select new ProfileRevisionRow(profile.State, revision == null ? null : revision.Id))
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (row == null) return new ResolvedProfileRevision(null, Problem(ArtifactCasProblemCode.ProfileMissing));
        if (row.State != StorageProfileState.Active) return new ResolvedProfileRevision(null, Problem(ArtifactCasProblemCode.ProfileNotActive));
        return row.RevisionId == null
            ? new ResolvedProfileRevision(null, Problem(ArtifactCasProblemCode.ProfileRevisionMissing))
            : new ResolvedProfileRevision(row.RevisionId, null);
    }

    private async Task<DriverCreation> OpenDriverAsync(DriverActivationRequest request, CancellationToken cancellationToken)
    {
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(request.Timeout);
        Task<StorageRuntimeDriverResolution>? pending = null;
        try
        {
            pending = _driverBroker.OpenAsync(new StorageRuntimeDriverRequest(request.TeamId, request.ProfileId, request.ProfileRevision), timeoutSource.Token).AsTask();
            var resolution = await pending.WaitAsync(timeoutSource.Token).ConfigureAwait(false);
            if (resolution is not StorageRuntimeDriverResolution.Ready ready)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (timeoutSource.IsCancellationRequested)
                    return new DriverCreation(null, Problem(ArtifactCasProblemCode.ProviderTimeout, true));
                return new DriverCreation(null, MapBrokerFailure(resolution));
            }
            if (cancellationToken.IsCancellationRequested)
            {
                await DisposeLeaseQuietlyAsync(ready.Lease).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
            }
            if (timeoutSource.IsCancellationRequested)
            {
                await DisposeLeaseQuietlyAsync(ready.Lease).ConfigureAwait(false);
                return new DriverCreation(null, Problem(ArtifactCasProblemCode.ProviderTimeout, true));
            }
            var capabilityProblem = await RequireCapabilitiesAsync(ready.Lease, request.RequiredCapabilities).ConfigureAwait(false);
            return capabilityProblem == null ? new DriverCreation(ready.Lease, null) : new DriverCreation(null, capabilityProblem);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            ObserveLateBrokerResolution(pending);
            return new DriverCreation(null, Problem(ArtifactCasProblemCode.ProviderTimeout, true));
        }
        catch (OperationCanceledException)
        {
            ObserveLateBrokerResolution(pending);
            throw;
        }
        catch (Exception exception) when (IsRecoverable(exception))
        {
            ObserveLateBrokerResolution(pending);
            cancellationToken.ThrowIfCancellationRequested();
            if (timeoutSource.IsCancellationRequested)
                return new DriverCreation(null, Problem(ArtifactCasProblemCode.ProviderTimeout, true));
            return new DriverCreation(null, Problem(ArtifactCasProblemCode.ProviderFailure, true));
        }
    }

    internal static async Task<ArtifactCasProblem?> RequireCapabilitiesAsync(StorageRuntimeDriverLease lease, StorageProviderCapabilities requiredCapabilities)
    {
        StorageProviderCapabilities capabilities;
        try { capabilities = lease.Driver.Capabilities; }
        catch (Exception exception) when (IsRecoverable(exception))
        {
            await DisposeLeaseQuietlyAsync(lease).ConfigureAwait(false);
            return Problem(ArtifactCasProblemCode.ProviderFailure, true);
        }
        if ((capabilities & requiredCapabilities) == requiredCapabilities) return null;
        await DisposeLeaseQuietlyAsync(lease).ConfigureAwait(false);
        return Problem(ArtifactCasProblemCode.Unsupported);
    }

    private async Task<HashObservation> HashAsync(Stream stream, StorageRuntimeDriverLease driverLease, TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);
        // A dedicated bounded buffer is intentional: a provider stream may ignore cancellation and complete a timed
        // out read later. Pooling would let that late write corrupt another request's re-rented buffer.
        var buffer = new byte[HashBufferSize];
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        long size = 0;
        while (true)
        {
            int read;
            Task<int>? pending = null;
            var abandoned = false;
            try
            {
                pending = driverLease.Track(stream.ReadAsync(buffer.AsMemory(), timeoutSource.Token).AsTask());
                read = await pending.WaitAsync(timeoutSource.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                if (pending != null) { abandoned = true; driverLease.Abandon(pending); }
                return new HashObservation(null, 0, true, null);
            }
            catch (OperationCanceledException)
            {
                if (pending != null) { abandoned = true; driverLease.Abandon(pending); }
                throw;
            }
            catch (UnauthorizedAccessException)
            {
                return new HashObservation(null, 0, false, Problem(ArtifactCasProblemCode.Forbidden));
            }
            catch (IOException)
            {
                return new HashObservation(null, 0, false, Problem(ArtifactCasProblemCode.ProviderUnavailableTransient, true));
            }
            catch (Exception)
            {
                return new HashObservation(null, 0, false, Problem(ArtifactCasProblemCode.ProviderFailure, true));
            }
            finally
            {
                if (pending != null && !abandoned) driverLease.Release(pending);
            }
            if (read == 0) return new HashObservation(hash.GetHashAndReset(), size, false, null);
            hash.AppendData(buffer, 0, read);
            size += read;
        }
    }

    private static async Task<Invocation<T>> InvokeAsync<T>(Func<CancellationToken, ValueTask<T>> action, TimeSpan timeout, CancellationToken cancellationToken, StorageRuntimeDriverLease driverLease)
    {
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);
        Task<T>? pending = null;
        var abandoned = false;
        try
        {
            pending = driverLease.Track(action(timeoutSource.Token).AsTask());
            return new Invocation<T>(await pending.WaitAsync(timeoutSource.Token).ConfigureAwait(false), false, null);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            if (pending != null) { abandoned = true; driverLease.Abandon(pending); }
            return new Invocation<T>(default, true, null);
        }
        catch (OperationCanceledException)
        {
            if (pending != null) { abandoned = true; driverLease.Abandon(pending); }
            throw;
        }
        catch (UnauthorizedAccessException)
        {
            return new Invocation<T>(default, false, Problem(ArtifactCasProblemCode.Forbidden));
        }
        catch (NotSupportedException)
        {
            return new Invocation<T>(default, false, Problem(ArtifactCasProblemCode.Unsupported));
        }
        catch (InvalidDataException)
        {
            return new Invocation<T>(default, false, Problem(ArtifactCasProblemCode.TargetCorrupt));
        }
        catch (IOException)
        {
            return new Invocation<T>(default, false, Problem(ArtifactCasProblemCode.ProviderUnavailableTransient, true));
        }
        catch (Exception)
        {
            return new Invocation<T>(default, false, Problem(ArtifactCasProblemCode.ProviderFailure, true));
        }
        finally
        {
            if (pending != null && !abandoned) driverLease.Release(pending);
        }
    }

    /// <summary>
    /// A write consumes caller-owned bytes. After a timeout/cancellation signal we therefore wait for the provider
    /// task to settle before returning, so a non-conforming plugin cannot continue touching a stream the caller may
    /// now dispose. Qualified drivers settle promptly when their cancellation token is signalled.
    /// </summary>
    private static async Task<Invocation<T>> InvokeOwnedInputAsync<T>(Func<CancellationToken, ValueTask<T>> action, TimeSpan timeout, CancellationToken cancellationToken, StorageRuntimeDriverLease driverLease)
    {
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);
        Task<T>? pending = null;
        var abandoned = false;
        try
        {
            pending = driverLease.Track(action(timeoutSource.Token).AsTask());
            return new Invocation<T>(await pending.WaitAsync(timeoutSource.Token).ConfigureAwait(false), false, null);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            if (pending != null) { abandoned = true; driverLease.Abandon(pending); }
            await ObserveOwnedInputSettlementAsync(pending).ConfigureAwait(false);
            return new Invocation<T>(default, true, null);
        }
        catch (OperationCanceledException)
        {
            if (pending != null) { abandoned = true; driverLease.Abandon(pending); }
            await ObserveOwnedInputSettlementAsync(pending).ConfigureAwait(false);
            throw;
        }
        catch (UnauthorizedAccessException)
        {
            return new Invocation<T>(default, false, Problem(ArtifactCasProblemCode.Forbidden));
        }
        catch (NotSupportedException)
        {
            return new Invocation<T>(default, false, Problem(ArtifactCasProblemCode.Unsupported));
        }
        catch (InvalidDataException)
        {
            return new Invocation<T>(default, false, Problem(ArtifactCasProblemCode.TargetCorrupt));
        }
        catch (IOException)
        {
            return new Invocation<T>(default, false, Problem(ArtifactCasProblemCode.ProviderUnavailableTransient, true));
        }
        catch (Exception)
        {
            return new Invocation<T>(default, false, Problem(ArtifactCasProblemCode.ProviderFailure, true));
        }
        finally
        {
            if (pending != null && !abandoned) driverLease.Release(pending);
        }
    }

    private static async Task ObserveOwnedInputSettlementAsync<T>(Task<T>? pending)
    {
        if (pending == null) return;
        try { await pending.ConfigureAwait(false); }
        catch { /* Provider outcome is represented by the timeout/cancellation; never log provider exception text. */ }
    }

    private static ValidTransfer Validate(ArtifactCasTransferRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.TeamId == Guid.Empty || request.StorageProfileId == Guid.Empty || request.ActorId == Guid.Empty)
            throw new ArgumentException("Team, storage profile and actor ids are required.", nameof(request));
        if (request.StorageProfileRevision <= 0) throw new ArgumentOutOfRangeException(nameof(request), "A positive profile revision is required.");
        if (string.IsNullOrWhiteSpace(request.IdempotencyKey) || request.IdempotencyKey.Length > 256)
            throw new ArgumentException("A 1-256 character idempotency key is required.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.TargetObjectKey) || request.TargetObjectKey.Length > 2048)
            throw new ArgumentException("A 1-2048 character target object key is required.", nameof(request));
        if (request.Content == null || !request.Content.CanRead) throw new ArgumentException("A readable content stream is required.", nameof(request));
        if (request.ExpectedSizeBytes < 0) throw new ArgumentOutOfRangeException(nameof(request), "Expected size cannot be negative.");
        if (!TryDigest(request.ExpectedSha256, out var digest)) throw new ArgumentException("ExpectedSha256 must be exactly 64 hexadecimal characters.", nameof(request));
        Validate(request.ExecutionIdentity);
        return new ValidTransfer(digest, request.ExpectedSizeBytes, ValidateTimeout(request.OperationTimeout));
    }

    private static TimeSpan Validate(ArtifactCasReadRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.TeamId == Guid.Empty || request.ArtifactObjectId == Guid.Empty || request.StorageProfileId == Guid.Empty)
            throw new ArgumentException("Team, artifact object and storage profile ids are required.", nameof(request));
        if (request.StorageProfileRevision <= 0) throw new ArgumentOutOfRangeException(nameof(request), "A positive profile revision is required.");
        return ValidateTimeout(request.OperationTimeout);
    }

    private static TimeSpan ValidateTimeout(TimeSpan? timeout)
    {
        var value = timeout ?? DefaultOperationTimeout;
        if (value <= TimeSpan.Zero || value > MaximumOperationTimeout)
            throw new ArgumentOutOfRangeException(nameof(timeout), $"Operation timeout must be positive and no greater than {MaximumOperationTimeout}.");
        return value;
    }

    private static void Validate(ArtifactCasExecutionIdentity? identity)
    {
        if (identity == null) return;
        if (identity.AttemptId == Guid.Empty || identity.AttemptOrdinal <= 0 || identity.Generation <= 0)
            throw new ArgumentException("Execution identity requires a non-empty attempt and positive ordinal/generation.", nameof(identity));
    }

    private static bool Matches(ArtifactTransferIntent intent, ArtifactCasTransferRequest request, byte[] digest) =>
        intent.ExpectedDigestAlgorithm == ArtifactDigestAlgorithm.Sha256 && intent.ExpectedDigest.AsSpan().SequenceEqual(digest)
        && intent.ExpectedSizeBytes == request.ExpectedSizeBytes && string.Equals(intent.TargetLocator, request.TargetObjectKey, StringComparison.Ordinal)
        && string.Equals(intent.TargetObjectKey, request.TargetObjectKey, StringComparison.Ordinal)
        && intent.ExecutionAttemptId == request.ExecutionIdentity?.AttemptId && intent.ExecutionAttemptOrdinal == request.ExecutionIdentity?.AttemptOrdinal
        && intent.ExecutionGeneration == request.ExecutionIdentity?.Generation;

    private static bool Reusable(ArtifactLocation location, ArtifactObject artifact, IntentSnapshot claim) =>
        location.ArtifactObjectId == artifact.Id && location.State == ArtifactLocationState.Available
        && string.Equals(location.Locator, claim.Locator, StringComparison.Ordinal) && location.ObservedSizeBytes == claim.Size
        && string.Equals(location.ProviderChecksumAlgorithm, "Sha256", StringComparison.Ordinal) && location.ProviderChecksum.AsSpan().SequenceEqual(claim.Digest);

    private static bool HeadCanMatch(string objectKey, ValidTransfer input, ArtifactStorageObjectMetadata metadata) =>
        string.Equals(metadata.ObjectKey, objectKey, StringComparison.Ordinal) && metadata.Length == input.Size
        && (metadata.Sha256 == null || string.Equals(metadata.Sha256, Convert.ToHexStringLower(input.Digest), StringComparison.OrdinalIgnoreCase));

    private static bool MetadataMatches(ReadLocation stored, ArtifactStorageObjectMetadata metadata) =>
        string.Equals(metadata.ObjectKey, stored.ObjectKey, StringComparison.Ordinal) && metadata.Length == stored.Size
        && (metadata.Sha256 == null || string.Equals(metadata.Sha256, Convert.ToHexStringLower(stored.Digest), StringComparison.OrdinalIgnoreCase))
        && (stored.ProviderETag == null || string.Equals(metadata.ETag, stored.ProviderETag, StringComparison.Ordinal))
        && (stored.ProviderObjectVersion == null || string.Equals(metadata.Version, stored.ProviderObjectVersion, StringComparison.Ordinal));

    private static bool MetadataAgrees(ArtifactStorageObjectMetadata head, ArtifactStorageObjectMetadata opened, string objectKey) =>
        string.Equals(head.ObjectKey, objectKey, StringComparison.Ordinal) && string.Equals(opened.ObjectKey, objectKey, StringComparison.Ordinal)
        && head.Length == opened.Length && string.Equals(head.ETag, opened.ETag, StringComparison.Ordinal)
        && string.Equals(head.Version, opened.Version, StringComparison.Ordinal)
        && (head.Sha256 == null || opened.Sha256 == null || string.Equals(head.Sha256, opened.Sha256, StringComparison.OrdinalIgnoreCase));

    private static bool TryDigest(string value, out byte[] digest)
    {
        digest = [];
        if (value == null || value.Length != 64) return false;
        try
        {
            digest = Convert.FromHexString(value);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static ArtifactLocationEvent Event(ArtifactLocation location, Guid actorId) => new()
    {
        Id = Guid.NewGuid(), TeamId = location.TeamId, ArtifactLocationId = location.Id, Revision = location.Revision,
        EventType = ArtifactLocationEventType.Verified, State = location.State, ObservedAt = location.VerifiedAt!.Value,
        ProviderObjectVersion = location.ProviderObjectVersion, ProviderETag = location.ProviderETag,
        ProviderChecksumAlgorithm = location.ProviderChecksumAlgorithm, ProviderChecksum = location.ProviderChecksum,
        ObservedSizeBytes = location.ObservedSizeBytes, VerifiedAt = location.VerifiedAt,
        ContentEncoding = location.ContentEncoding, EncryptionKeyVersion = location.EncryptionKeyVersion,
        DetailsJson = "{}", CreatedBy = actorId,
    };

    internal static ArtifactCasProblem MapBrokerFailure(StorageRuntimeDriverResolution resolution) => resolution switch
    {
        StorageRuntimeDriverResolution.ProfileUnavailable value => MapProfileFailure(value.Reason),
        StorageRuntimeDriverResolution.CredentialUnavailable value => MapCredentialFailure(value.Reason),
        StorageRuntimeDriverResolution.ProviderUnavailable value => MapProviderFailure(value.Reason),
        StorageRuntimeDriverResolution.ConfigurationInvalid value => MapConfigurationFailure(value.Reason),
        StorageRuntimeDriverResolution.Cancelled value => MapCancellation(value.Stage),
        StorageRuntimeDriverResolution.DriverInitializationFailed value => MapDriverFailure(value.Reason),
        StorageRuntimeDriverResolution.Ready => Problem(ArtifactCasProblemCode.ProviderFailure),
        _ => Problem(ArtifactCasProblemCode.ProviderFailure),
    };

    private static ArtifactCasProblem MapProfileFailure(StorageRuntimeProfileFailureReason reason) => reason switch
    {
        StorageRuntimeProfileFailureReason.Missing => Problem(ArtifactCasProblemCode.ProfileMissing),
        StorageRuntimeProfileFailureReason.NotActive => Problem(ArtifactCasProblemCode.ProfileNotActive),
        StorageRuntimeProfileFailureReason.RevisionMissing => Problem(ArtifactCasProblemCode.ProfileRevisionMissing),
        StorageRuntimeProfileFailureReason.ResolutionFailed => Problem(ArtifactCasProblemCode.ProviderUnavailableTransient, true),
        _ => Problem(ArtifactCasProblemCode.ProviderFailure),
    };

    private static ArtifactCasProblem MapCredentialFailure(StorageRuntimeCredentialFailureReason reason) => reason switch
    {
        StorageRuntimeCredentialFailureReason.Missing => Problem(ArtifactCasProblemCode.CredentialUnavailable),
        StorageRuntimeCredentialFailureReason.NotActive => Problem(ArtifactCasProblemCode.CredentialUnavailable),
        StorageRuntimeCredentialFailureReason.RevisionMissing => Problem(ArtifactCasProblemCode.CredentialUnavailable),
        StorageRuntimeCredentialFailureReason.ProviderMismatch => Problem(ArtifactCasProblemCode.CredentialInvalid),
        StorageRuntimeCredentialFailureReason.ProviderUnavailable => Problem(ArtifactCasProblemCode.CredentialBrokerUnavailable, true),
        StorageRuntimeCredentialFailureReason.InvalidEnvelope => Problem(ArtifactCasProblemCode.CredentialInvalid),
        StorageRuntimeCredentialFailureReason.InvalidReference => Problem(ArtifactCasProblemCode.CredentialInvalid),
        StorageRuntimeCredentialFailureReason.InvalidSecret => Problem(ArtifactCasProblemCode.CredentialInvalid),
        StorageRuntimeCredentialFailureReason.ResolutionFailed => Problem(ArtifactCasProblemCode.CredentialBrokerUnavailable, true),
        _ => Problem(ArtifactCasProblemCode.ProviderFailure),
    };

    private static ArtifactCasProblem MapProviderFailure(StorageRuntimeProviderFailureReason reason) => reason switch
    {
        StorageRuntimeProviderFailureReason.ModuleMissing => Problem(ArtifactCasProblemCode.ProviderUnavailable),
        StorageRuntimeProviderFailureReason.FactoryMissing => Problem(ArtifactCasProblemCode.ProviderUnavailable),
        StorageRuntimeProviderFailureReason.FactoryMismatch => Problem(ArtifactCasProblemCode.ProviderUnavailable),
        StorageRuntimeProviderFailureReason.CatalogFailure => Problem(ArtifactCasProblemCode.ProviderFailure, true),
        _ => Problem(ArtifactCasProblemCode.ProviderFailure),
    };

    private static ArtifactCasProblem MapConfigurationFailure(StorageRuntimeConfigurationFailureReason reason) => reason switch
    {
        StorageRuntimeConfigurationFailureReason.InvalidConfiguration => Problem(ArtifactCasProblemCode.ProfileInvalid),
        StorageRuntimeConfigurationFailureReason.UnsupportedSchemaVersion => Problem(ArtifactCasProblemCode.ProfileInvalid),
        StorageRuntimeConfigurationFailureReason.SnapshotIdentityMismatch => Problem(ArtifactCasProblemCode.ProfileInvalid),
        StorageRuntimeConfigurationFailureReason.InvalidProviderTypeKey => Problem(ArtifactCasProblemCode.ProfileInvalid),
        StorageRuntimeConfigurationFailureReason.FactoryRejectedConfiguration => Problem(ArtifactCasProblemCode.Unsupported),
        _ => Problem(ArtifactCasProblemCode.ProviderFailure),
    };

    private static ArtifactCasProblem MapCancellation(StorageRuntimeCancellationStage stage) => stage switch
    {
        StorageRuntimeCancellationStage.ProfileResolution => Problem(ArtifactCasProblemCode.ProviderTimeout, true),
        StorageRuntimeCancellationStage.CredentialResolution => Problem(ArtifactCasProblemCode.ProviderTimeout, true),
        StorageRuntimeCancellationStage.DriverInitialization => Problem(ArtifactCasProblemCode.ProviderTimeout, true),
        _ => Problem(ArtifactCasProblemCode.ProviderFailure),
    };

    private static ArtifactCasProblem MapDriverFailure(StorageRuntimeDriverInitializationFailureReason reason) => reason switch
    {
        StorageRuntimeDriverInitializationFailureReason.NullDriver => Problem(ArtifactCasProblemCode.ProviderFailure, true),
        StorageRuntimeDriverInitializationFailureReason.ProviderCanceled => Problem(ArtifactCasProblemCode.ProviderTimeout, true),
        StorageRuntimeDriverInitializationFailureReason.ProviderFailure => Problem(ArtifactCasProblemCode.ProviderFailure, true),
        StorageRuntimeDriverInitializationFailureReason.CleanupFailure => Problem(ArtifactCasProblemCode.ProviderFailure, true),
        _ => Problem(ArtifactCasProblemCode.ProviderFailure),
    };

    private static ArtifactCasProblem Map(ArtifactStorageError error, bool readMissing = false) => error.Code switch
    {
        ArtifactStorageErrorCode.Missing => Problem(readMissing ? ArtifactCasProblemCode.TargetMissing : ArtifactCasProblemCode.TargetMissing, true),
        ArtifactStorageErrorCode.IntegrityMismatch or ArtifactStorageErrorCode.Corrupt => Problem(ArtifactCasProblemCode.TargetCorrupt),
        ArtifactStorageErrorCode.Unauthorized => Problem(ArtifactCasProblemCode.Unauthorized),
        ArtifactStorageErrorCode.Forbidden => Problem(ArtifactCasProblemCode.Forbidden),
        ArtifactStorageErrorCode.Throttled => Problem(ArtifactCasProblemCode.Throttled, true),
        ArtifactStorageErrorCode.Unavailable => Problem(ArtifactCasProblemCode.ProviderUnavailableTransient, true),
        ArtifactStorageErrorCode.Unsupported => Problem(ArtifactCasProblemCode.Unsupported),
        ArtifactStorageErrorCode.ConditionNotMet when readMissing => Problem(ArtifactCasProblemCode.TargetCorrupt),
        ArtifactStorageErrorCode.AlreadyExists or ArtifactStorageErrorCode.ConditionNotMet => Problem(ArtifactCasProblemCode.IdempotencyConflict),
        _ => Problem(ArtifactCasProblemCode.ProviderFailure, error.IsRetryable),
    };

    private static ArtifactCasProblem StoredProblem(IntentSnapshot intent) => Enum.TryParse<ArtifactCasProblemCode>(intent.LastErrorCode, out var code)
        ? Problem(code, intent.State == ArtifactTransferState.RetryScheduled)
        : Problem(ArtifactCasProblemCode.ProviderFailure, intent.State == ArtifactTransferState.RetryScheduled);

    private static ArtifactCasProblem Problem(ArtifactCasProblemCode code, bool retryable = false) => new(code, retryable);
    private static string SafeMessage(ArtifactCasProblemCode code) => $"Artifact CAS transfer stopped with typed outcome '{code}'.";
    private static TimeSpan RetryDelay(int retryCount) => TimeSpan.FromSeconds(Math.Min(300, Math.Pow(2, Math.Min(retryCount, 8))));
    private static TimeSpan LeaseDuration(TimeSpan timeout) => timeout + (timeout > MinimumLeaseMargin ? timeout : MinimumLeaseMargin);
    private static bool LeaseIsCurrent(ArtifactTransferIntent intent, long? fence, DateTimeOffset now) =>
        fence != null && intent.WorkerFenceEpoch == fence && intent.WorkerLeaseExpiresAt > now;
    private ArtifactCasTransferResult.Deferred Stale(Guid intentId) => new(intentId, _clock.GetUtcNow(), Problem(ArtifactCasProblemCode.StaleWorker, true));
    private static bool IsUniqueViolation(Exception exception) => exception is DbUpdateException { InnerException: PostgresException { SqlState: PostgresErrorCodes.UniqueViolation } } || exception is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation };

    private static void ObserveLateBrokerResolution(Task<StorageRuntimeDriverResolution>? pending)
    {
        if (pending != null) _ = DisposeLateBrokerLeaseAsync(pending);
    }

    private static async Task DisposeLateBrokerLeaseAsync(Task<StorageRuntimeDriverResolution> pending)
    {
        try
        {
            if (await pending.ConfigureAwait(false) is StorageRuntimeDriverResolution.Ready ready)
                await DisposeLeaseQuietlyAsync(ready.Lease).ConfigureAwait(false);
        }
        catch { /* Observe late broker faults without logging provider, configuration or secret material. */ }
    }

    private static async Task DisposeLeaseQuietlyAsync(StorageRuntimeDriverLease lease)
    {
        try { await lease.DisposeAsync().ConfigureAwait(false); }
        catch { /* Cleanup cannot change the already-typed CAS outcome or expose provider detail. */ }
    }

    private static bool IsRecoverable(Exception exception) => exception is not OutOfMemoryException and not AccessViolationException;

    private CodeSpaceDbContext CreateDb() => new(_dbOptions);

    private static Task<DateTimeOffset> DatabaseClockAsync(CodeSpaceDbContext db, CancellationToken cancellationToken) =>
        db.Database.SqlQueryRaw<DateTimeOffset>("SELECT clock_timestamp() AS \"Value\"").SingleAsync(cancellationToken);

    private static IntentSnapshot Snapshot(ArtifactTransferIntent intent, ArtifactCasProblem? problem) => new()
    {
        Id = intent.Id,
        TeamId = intent.TeamId,
        ProfileRevisionId = intent.StorageProfileRevisionId,
        State = intent.State,
        Fence = intent.WorkerFenceEpoch,
        Digest = intent.ExpectedDigest,
        Size = intent.ExpectedSizeBytes,
        Locator = intent.TargetLocator,
        ObjectKey = intent.TargetObjectKey,
        ArtifactObjectId = intent.ArtifactObjectId,
        ArtifactLocationId = intent.ArtifactLocationId,
        NextAttemptAt = intent.NextAttemptAt,
        LeaseExpiresAt = intent.WorkerLeaseExpiresAt,
        LastErrorCode = intent.LastErrorCode,
        Problem = problem,
    };

    private sealed record ResolvedProfileRevision(Guid? ProfileRevisionId, ArtifactCasProblem? Problem);
    private sealed record DriverActivationRequest(Guid TeamId, Guid ProfileId, int ProfileRevision, TimeSpan Timeout, StorageProviderCapabilities RequiredCapabilities);
    private sealed record DriverCreation(StorageRuntimeDriverLease? Lease, ArtifactCasProblem? Problem);
    private sealed record ProfileRevisionRow(StorageProfileState State, Guid? RevisionId);
    private sealed record ClaimResult(IntentSnapshot Intent, bool Acquired);
    private sealed record ValidTransfer(byte[] Digest, long Size, TimeSpan Timeout);
    private sealed record LeaseRenewal(IntentSnapshot Claim, Guid ActorId);
    private sealed record Verification(ArtifactStorageObjectMetadata? Metadata, ArtifactCasProblem? Problem);
    private sealed record Invocation<T>(T? Value, bool Timeout, ArtifactCasProblem? Problem);
    private sealed record HashObservation(byte[]? Digest, long Size, bool Timeout, ArtifactCasProblem? Problem);
    private sealed record ReadLocation(string ObjectKey, string? ProviderETag, string? ProviderObjectVersion, long Size, byte[] Digest);
    private sealed record IntentSnapshot
    {
        public required Guid Id { get; init; }
        public required Guid TeamId { get; init; }
        public required Guid ProfileRevisionId { get; init; }
        public required ArtifactTransferState State { get; init; }
        public long? Fence { get; init; }
        public required byte[] Digest { get; init; }
        public required long Size { get; init; }
        public required string Locator { get; init; }
        public required string ObjectKey { get; init; }
        public Guid? ArtifactObjectId { get; init; }
        public Guid? ArtifactLocationId { get; init; }
        public DateTimeOffset? NextAttemptAt { get; init; }
        public DateTimeOffset? LeaseExpiresAt { get; init; }
        public string? LastErrorCode { get; init; }
        public ArtifactCasProblem? Problem { get; init; }
        public bool IsStale { get; init; }
    }
}
