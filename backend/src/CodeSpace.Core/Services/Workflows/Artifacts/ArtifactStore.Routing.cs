using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Workflows.Artifacts.Exceptions;
using CodeSpace.Core.Services.Workflows.Artifacts.Runtime;
using CodeSpace.Messages.Constants;
using Microsoft.EntityFrameworkCore;

namespace CodeSpace.Core.Services.Workflows.Artifacts;

/// <summary>
/// Where an offloaded artifact's bytes GO, and how they come back. The route is consulted exactly once per write and
/// the profile revision it names is stamped onto the durable <c>artifact_location</c> the CAS runtime writes; a read
/// resolves through the recorded locations of that object and never through today's routing policy, so repointing,
/// disabling or retiring a route can never change where existing bytes are looked for. A team with no route keeps the
/// local backend verbatim.
/// </summary>
public sealed partial class ArtifactStore
{
    /// <summary>
    /// How long a routed write waits for a CONCURRENT writer of the same content before giving up. Sized to outlast
    /// the CAS worker lease (the coordinator's default operation timeout, doubled), so a writer that dies mid-transfer
    /// does not strand every other writer of those bytes: once the lease lapses the waiter claims the abandoned intent
    /// and completes the placement itself. Waiting beats throwing — throwing discards the caller's contribution for
    /// content that another caller is in the middle of storing.
    /// </summary>
    private static readonly TimeSpan RoutedWaitBudget = TimeSpan.FromMinutes(4);
    private static readonly TimeSpan RoutedPollFloor = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan RoutedPollCeiling = TimeSpan.FromSeconds(5);

    /// <summary>Object key inside the profile's namespace. Content-addressed and sharded like the local backend, so a re-put of the same bytes targets the same object.</summary>
    private static string ObjectKeyFor(string sha256) => $"workflow-artifacts/{sha256[..2]}/{sha256.Substring(2, 2)}/{sha256}";

    /// <summary>
    /// An offloaded row is read through the destination IT recorded, never through today's policy: a local row keeps
    /// resolving its <c>storage_url</c> even after the team adopts a route, and a routed row resolves through the
    /// <c>artifact_location</c> rows its own object carries, even after the route is repointed or retired.
    /// </summary>
    private async Task<byte[]> ReadOffloadedAsync(Guid teamId, WorkflowArtifact row, CancellationToken cancellationToken)
    {
        if (row.StorageUrl is { } url) return await _blobs.ReadAsync(url, cancellationToken).ConfigureAwait(false);

        if (row.CasArtifactObjectId is not { } artifactObjectId)
            throw new InvalidOperationException($"Artifact {row.Id} has neither inline bytes, a storage_url, nor a routed storage object.");

        return await ReadRoutedAsync(new RoutedRead(teamId, row.Id, artifactObjectId), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Resolves the destination for ONE offloaded write and places the bytes there. Fails closed — never a silent local fallback.</summary>
    private async Task<ArtifactPlacement> PlaceOffloadedAsync(OffloadedWrite write, CancellationToken cancellationToken)
    {
        var destination = await _destinations.ResolveAsync(write.TeamId, cancellationToken).ConfigureAwait(false);

        // Exhaustive by CASE, not by negation. A destination kind added later must fail closed here rather than fall
        // through to a local-disk write, which is the exact silent fallback this plane exists to remove.
        return destination switch
        {
            WorkflowArtifactDestination.Local => ArtifactPlacement.Local(await _blobs.WriteAsync(write.Sha, write.Bytes, cancellationToken).ConfigureAwait(false)),
            WorkflowArtifactDestination.Routed routed => ArtifactPlacement.Routed(await TransferAsync(write, routed, cancellationToken).ConfigureAwait(false)),
            WorkflowArtifactDestination.Unusable unusable => throw new ArtifactStorageDestinationUnavailableException(write.TeamId, unusable.Problem),
            _ => throw new ArtifactStorageDestinationUnavailableException(write.TeamId, WorkflowArtifactDestinationProblem.ResolutionFailed),
        };
    }

    /// <summary>
    /// Places one routed write. The intent key is derived from the content, so concurrent writers of identical
    /// payloads — the normal case for a fan-out whose branches emit the same prompt, file body or transcript prefix —
    /// converge on ONE intent, and only one of them can hold its lease.
    ///
    /// <para><c>Deferred</c> therefore means "someone else is storing your bytes", not "your write failed": we wait
    /// for their commit and return their object id, upholding the same contract the <c>(team, sha)</c> row insert
    /// already upholds when it loses that race — PutAsync is idempotent and ALWAYS returns a valid id for the given
    /// content. A <c>Rejected</c> transfer, and an exhausted wait, raise the typed failure.</para>
    /// </summary>
    private async Task<Guid> TransferAsync(OffloadedWrite write, WorkflowArtifactDestination.Routed routed, CancellationToken cancellationToken)
    {
        var key = await IdempotencyKeyAsync(write, routed, cancellationToken).ConfigureAwait(false);
        var attempt = new RoutedAttempt(write, routed, key, write.Bytes.ToArray());

        var deadline = _clock.GetUtcNow() + RoutedWaitBudget;
        var backoff = RoutedPollFloor;

        while (true)
        {
            var transfer = await PutOnceAsync(attempt, cancellationToken).ConfigureAwait(false);

            if (transfer is ArtifactCasTransferResult.Committed committed) return committed.ArtifactObjectId;

            if (transfer is not ArtifactCasTransferResult.Deferred || _clock.GetUtcNow() >= deadline)
                throw new ArtifactStorageDestinationUnavailableException(write.TeamId, ProblemOf(transfer));

            await Task.Delay(backoff, _clock, cancellationToken).ConfigureAwait(false);
            backoff = backoff < RoutedPollCeiling ? backoff + backoff : RoutedPollCeiling;
        }
    }

    /// <summary>One transfer attempt. The stream is rebuilt per attempt (a previous attempt may have consumed it) over one shared payload copy.</summary>
    private async Task<ArtifactCasTransferResult> PutOnceAsync(RoutedAttempt attempt, CancellationToken cancellationToken)
    {
        using var content = new MemoryStream(attempt.Payload, writable: false);

        return await _routed.Transfers.PutAsync(new ArtifactCasTransferRequest
        {
            TeamId = attempt.Write.TeamId, StorageProfileId = attempt.Routed.StorageProfileId, StorageProfileRevision = attempt.Routed.StorageProfileRevision,
            IdempotencyKey = attempt.IdempotencyKey, TargetObjectKey = ObjectKeyFor(attempt.Write.Sha),
            Content = content, ExpectedSizeBytes = attempt.Payload.Length, ExpectedSha256 = attempt.Write.Sha,
            ContentType = attempt.Write.ContentType, ActorId = SystemUsers.SeederId,
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The intent key for THIS attempt: the content, plus a generation that steps over every intent a non-retryable
    /// problem already drove to <c>Failed</c> for the same content under the same profile revision.
    ///
    /// <para>The generation exists because <c>Failed</c> is a one-way door in the database, not merely in the code.
    /// <c>artifact_cas_transfer_guard</c> (0131_artifact_transfer_fence_claim.sql) refuses every route back out of it:
    /// a fence claim raises <c>'terminal rows cannot be claimed'</c> when <c>OLD.state IN ('Committed','Failed',
    /// 'Cancelled')</c>; a plain transition first demands <c>'saga transition requires an unexpired worker lease'</c>,
    /// which a Failed row can never satisfy because the same trigger forbids a terminal row from holding one; and the
    /// transition whitelist has no arm whose <c>OLD.state</c> is <c>'Failed'</c>. So the intent cannot move backwards
    /// — the repaired attempt has to be a NEW intent, and only a distinct idempotency key can mint one under
    /// <c>ux_artifact_transfer_intent_idempotency (team_id, storage_profile_revision_id, idempotency_key)</c>.</para>
    ///
    /// <para>Repairing what broke the transfer is exactly what does NOT bump <c>storage_profile_revision</c> — a
    /// corrected credential, a remounted volume, a bucket policy fix all leave the profile revision untouched — so
    /// without this the first write under a misconfiguration would ban those exact bytes for the team forever.
    /// <c>TargetObjectKey</c> is deliberately NOT generation-aware: every generation targets the same
    /// content-addressed object, so a retry that finds the object already there is provider-side dedup, not a
    /// duplicate upload.</para>
    ///
    /// <para><c>Cancelled</c> is deliberately not stepped over: it is an explicit stop rather than a fault, and
    /// nothing in this codebase produces it today.</para>
    /// </summary>
    private async Task<string> IdempotencyKeyAsync(OffloadedWrite write, WorkflowArtifactDestination.Routed routed, CancellationToken cancellationToken)
    {
        var content = IdempotencyKeyFor(write.Sha, generation: 0);

        var burned = await (from intent in _db.ArtifactTransferIntent.AsNoTracking()
                            join revision in _db.StorageProfileRevision.AsNoTracking()
                                on new { intent.TeamId, Id = intent.StorageProfileRevisionId } equals new { revision.TeamId, revision.Id }
                            where intent.TeamId == write.TeamId && revision.StorageProfileId == routed.StorageProfileId
                                && revision.Revision == routed.StorageProfileRevision
                                && intent.State == ArtifactTransferState.Failed && intent.IdempotencyKey.StartsWith(content)
                            select intent.Id).CountAsync(cancellationToken).ConfigureAwait(false);

        return IdempotencyKeyFor(write.Sha, burned);
    }

    /// <summary>
    /// One attempt generation's intent key. Generation 0 is the bare content key, so the shared-intent behaviour every
    /// concurrent writer depends on is the default and a healthy destination never mints a second key. The sha is
    /// fixed-width hex, so no generation of one payload can ever prefix-collide with another payload's key.
    /// </summary>
    internal static string IdempotencyKeyFor(string sha, int generation) => generation == 0
        ? $"{WorkflowArtifactDestinationResolver.DataClassTypeKey}/{sha}"
        : $"{WorkflowArtifactDestinationResolver.DataClassTypeKey}/{sha}/g{generation}";

    private static ArtifactCasProblemCode ProblemOf(ArtifactCasTransferResult transfer) => transfer switch
    {
        ArtifactCasTransferResult.Deferred deferred => deferred.Problem.Code,
        ArtifactCasTransferResult.Rejected rejected => rejected.Problem.Code,
        _ => ArtifactCasProblemCode.ProviderFailure,
    };

    /// <summary>Whole-object read for a routed row, verified end-to-end by the CAS stream before the store's own identity check.</summary>
    private async Task<byte[]> ReadRoutedAsync(RoutedRead read, CancellationToken cancellationToken)
    {
        var opened = await OpenRoutedAsync(read, cancellationToken).ConfigureAwait(false);

        await using var content = opened.Content;
        using var buffer = new MemoryStream();
        try
        {
            await content.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidDataException ex)
        {
            throw new ArtifactContentUnavailableException(read.ArtifactId, ArtifactContentUnavailableKind.IntegrityFailure, ex);
        }

        return buffer.ToArray();
    }

    /// <summary>
    /// Bounded read for a routed row. The provider is asked for the WINDOW — the whole-object stream is forward-only,
    /// so slicing it would re-read every preceding byte on every page and turn one viewer scroll of a large transcript
    /// into gigabytes of provider traffic. Partial ranges are unverified exactly as the local backend's are; the
    /// caller's own size/sha checks decide.
    /// </summary>
    private async Task<ArtifactBlobRange> ReadRoutedRangeAsync(RoutedRead read, long offset, int length, CancellationToken cancellationToken)
    {
        var stamps = await RecordedStampsAsync(read, cancellationToken).ConfigureAwait(false);
        var problem = ArtifactCasProblemCode.ArtifactMissing;

        foreach (var stamp in stamps)
        {
            var result = await _routed.Ranges.ReadRangeAsync(RangeRequestFor(read, stamp, offset, length), cancellationToken).ConfigureAwait(false);
            if (result is ArtifactCasRangeResult.Available available) return new ArtifactBlobRange(available.Bytes, available.TotalLength);

            problem = ((ArtifactCasRangeResult.Unavailable)result).Problem.Code;
        }

        throw new ArtifactContentUnavailableException(read.ArtifactId, KindOf(problem));
    }

    private static ArtifactCasRangeRequest RangeRequestFor(RoutedRead read, RecordedStamp stamp, long offset, int length) => new()
    {
        TeamId = read.TeamId, ArtifactObjectId = read.ArtifactObjectId,
        StorageProfileId = stamp.StorageProfileId, StorageProfileRevision = stamp.StorageProfileRevision,
        Offset = offset, Length = length,
    };

    /// <summary>Opens the object through a profile revision its own location ledger records — current routing policy is never consulted on a read.</summary>
    private async Task<ArtifactCasReadResult.Opened> OpenRoutedAsync(RoutedRead read, CancellationToken cancellationToken)
    {
        var stamps = await RecordedStampsAsync(read, cancellationToken).ConfigureAwait(false);
        if (stamps.Count == 0)
            throw new ArtifactContentUnavailableException(read.ArtifactId, ArtifactContentUnavailableKind.PhysicalObjectMissing);

        var problem = ArtifactCasProblemCode.ArtifactMissing;
        foreach (var stamp in stamps)
        {
            var result = await _routed.Transfers.OpenReadAsync(new ArtifactCasReadRequest
            {
                TeamId = read.TeamId, ArtifactObjectId = read.ArtifactObjectId,
                StorageProfileId = stamp.StorageProfileId, StorageProfileRevision = stamp.StorageProfileRevision,
            }, cancellationToken).ConfigureAwait(false);

            if (result is ArtifactCasReadResult.Opened opened) return opened;

            problem = ((ArtifactCasReadResult.Unavailable)result).Problem.Code;
        }

        throw new ArtifactContentUnavailableException(read.ArtifactId, KindOf(problem));
    }

    /// <summary>
    /// Every profile revision this object is durably recorded under, freshest observation first. The row itself stores
    /// only the object id, so this is "any Available location for these bytes", NOT "the one location the write
    /// stamped" — if a future replication or backfill adds a second location for the same object, this read follows
    /// the freshest of them. What routing state says TODAY is never consulted either way.
    /// </summary>
    private async Task<IReadOnlyList<RecordedStamp>> RecordedStampsAsync(RoutedRead read, CancellationToken cancellationToken)
    {
        var stamps = await (from location in _db.ArtifactLocation.AsNoTracking()
                            join revision in _db.StorageProfileRevision.AsNoTracking()
                                on new { location.TeamId, Id = location.StorageProfileRevisionId } equals new { revision.TeamId, revision.Id }
                            where location.TeamId == read.TeamId && location.ArtifactObjectId == read.ArtifactObjectId
                                && location.State == ArtifactLocationState.Available
                            orderby location.VerifiedAt descending, location.Id
                            select new RecordedStamp(revision.StorageProfileId, revision.Revision))
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        return stamps.Distinct().ToArray();
    }

    private static ArtifactContentUnavailableKind KindOf(ArtifactCasProblemCode problem) => problem switch
    {
        ArtifactCasProblemCode.ArtifactMissing or ArtifactCasProblemCode.TargetMissing => ArtifactContentUnavailableKind.PhysicalObjectMissing,
        ArtifactCasProblemCode.TargetCorrupt => ArtifactContentUnavailableKind.IntegrityFailure,
        ArtifactCasProblemCode.Unauthorized or ArtifactCasProblemCode.Forbidden or ArtifactCasProblemCode.CredentialUnavailable
            or ArtifactCasProblemCode.CredentialInvalid or ArtifactCasProblemCode.CredentialBrokerUnavailable => ArtifactContentUnavailableKind.AccessDenied,
        _ => ArtifactContentUnavailableKind.BackendUnavailable,
    };

    private static ArtifactRangeReadState RangeStateOf(ArtifactContentUnavailableKind kind) => kind switch
    {
        ArtifactContentUnavailableKind.MetadataMissing => ArtifactRangeReadState.MetadataMissing,
        ArtifactContentUnavailableKind.PhysicalObjectMissing => ArtifactRangeReadState.PhysicalObjectMissing,
        ArtifactContentUnavailableKind.IntegrityFailure => ArtifactRangeReadState.IntegrityFailure,
        ArtifactContentUnavailableKind.AccessDenied => ArtifactRangeReadState.AccessDenied,
        _ => ArtifactRangeReadState.BackendUnavailable,
    };

    /// <summary>Which of the row's three mutually exclusive destinations one write chose.</summary>
    private sealed record ArtifactPlacement(string? StorageUrl, Guid? CasArtifactObjectId)
    {
        public static readonly ArtifactPlacement Inline = new(null, null);

        public static ArtifactPlacement Local(string storageUrl) => new(storageUrl, null);

        public static ArtifactPlacement Routed(Guid artifactObjectId) => new(null, artifactObjectId);
    }

    /// <summary>One offloaded write's coordinates, carried as a unit so the placement pipeline stays one step per line.</summary>
    private sealed record OffloadedWrite(Guid TeamId, string Sha, ReadOnlyMemory<byte> Bytes, string ContentType);

    /// <summary>One routed placement in flight: the write, the destination it resolved, the intent key this attempt generation owns, and the payload every retry re-streams.</summary>
    private sealed record RoutedAttempt(OffloadedWrite Write, WorkflowArtifactDestination.Routed Routed, string IdempotencyKey, byte[] Payload);

    /// <summary>One routed read's coordinates: the tenant, the row asking, and the object it points at.</summary>
    private sealed record RoutedRead(Guid TeamId, Guid ArtifactId, Guid ArtifactObjectId);

    private sealed record RecordedStamp(Guid StorageProfileId, int StorageProfileRevision);
}
