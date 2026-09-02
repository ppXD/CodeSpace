using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Workflows.Artifacts.Exceptions;
using CodeSpace.Core.Services.Workflows.Artifacts.Routing;
using CodeSpace.Core.Services.Workflows.Artifacts.Runtime;
using CodeSpace.Messages.Constants;
using Microsoft.EntityFrameworkCore;

namespace CodeSpace.Core.Services.Workflows.Artifacts;

/// <summary>
/// Where an offloaded artifact's bytes GO, and how they come back. The route is consulted exactly once per write and
/// the profile revision it names is stamped onto the durable <c>artifact_location</c> the CAS runtime writes; a read
/// resolves through the recorded locations of that object and never through today's routing policy, so repointing,
/// disabling or retiring a route can never change where existing bytes are looked for. A team with no route — or with
/// one it created and never activated — keeps the local backend verbatim.
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
    /// The idempotency scope one payload's transfer claims: this data class, plus the content sha. Two writers of the
    /// same bytes therefore share one intent, and the CAS runtime — which owns attempt generations — picks the exact
    /// key within this scope. The sha is fixed-width hex, so no payload's scope can ever prefix another's.
    /// </summary>
    internal static string IdempotencyScopeFor(string sha256) => $"{WorkflowArtifactDestinationResolver.DataClassTypeKey}/{sha256}";

    /// <summary>
    /// An offloaded row is read through the destination IT recorded, never through today's policy: a local row keeps
    /// resolving its <c>storage_url</c> even after the team adopts a route, and a routed row resolves through the
    /// <c>artifact_location</c> rows its own object carries, even after the route is repointed or retired.
    /// </summary>
    private async Task<byte[]> ReadOffloadedAsync(Guid teamId, WorkflowArtifact row, CancellationToken cancellationToken)
    {
        if (row.StorageUrl is { } url) return await ReadLocalAsync(row.Id, url, cancellationToken).ConfigureAwait(false);

        if (row.CasArtifactObjectId is not { } artifactObjectId)
            throw NoDestinationRecorded(row.Id);

        return await ReadRoutedAsync(new RoutedRead(teamId, row.Id, artifactObjectId), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// What a row that names nowhere actually is: the metadata saying where its bytes went is gone, which is the same
    /// lane a row that no longer exists reports. Typed at the SOURCE rather than left to the shared table, which sees
    /// this fault differently on each lane — the whole-object read throws it past no catch that consults the table at
    /// all, and the bounded read throws it into one that would flatten a bare <see cref="InvalidOperationException"/>
    /// into an integrity failure. Untyped it went straight out through the whole-object reader and cost the entire run
    /// detail. The three-way storage CHECK forbids the row, but it was added NOT VALID and never validated, so the
    /// guard is the app's to keep honest.
    ///
    /// <para>ONE factory because that row has TWO readers, each reaching it through its own guard: the whole-object
    /// read here, and the bounded read's locator check in <c>RoutedReadFor</c>. Typing one of them left the two
    /// answering differently for a single physical row — a disagreement nothing would have reported, and worse than
    /// both being wrong the same way. The kind is decided here, once, and both lanes inherit it.</para>
    /// </summary>
    private static Exception NoDestinationRecorded(Guid artifactId) =>
        new ArtifactContentUnavailableException(artifactId, ArtifactContentUnavailableKind.MetadataMissing,
            new InvalidOperationException($"Artifact {artifactId} has neither inline bytes, a storage_url, nor a routed storage object."));

    /// <summary>
    /// Whole-object read from the local backend, typed the way the routed path already types itself: a wiped root or a
    /// revoked permission is a storage-plane FACT, and letting it escape as a raw IO exception is what left the local
    /// lane — the shipped state of every unrouted team — as the only read with no verdict at all.
    /// </summary>
    private async Task<byte[]> ReadLocalAsync(Guid artifactId, string storageUrl, CancellationToken cancellationToken)
    {
        try
        {
            return await _blobs.ReadAsync(storageUrl, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ArtifactReadFailureClassifier.TryClassify(ex, out var kind))
        {
            throw new ArtifactContentUnavailableException(artifactId, kind, ex);
        }
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
    /// Places one routed write. The intent scope is derived from the content, so concurrent writers of identical
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
        var attempt = new RoutedAttempt(write, routed, write.Bytes.ToArray());

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
            IdempotencyScope = IdempotencyScopeFor(attempt.Write.Sha), TargetObjectKey = ObjectKeyFor(attempt.Write.Sha),
            Content = content, ExpectedSizeBytes = attempt.Payload.Length, ExpectedSha256 = attempt.Write.Sha,
            ContentType = attempt.Write.ContentType, ActorId = SystemUsers.SeederId,
        }, cancellationToken).ConfigureAwait(false);
    }

    private static ArtifactCasProblemCode ProblemOf(ArtifactCasTransferResult transfer) => transfer switch
    {
        ArtifactCasTransferResult.Deferred deferred => deferred.Problem.Code,
        ArtifactCasTransferResult.Rejected rejected => rejected.Problem.Code,
        _ => ArtifactCasProblemCode.ProviderFailure,
    };

    /// <summary>
    /// Whole-object read for a routed row, verified end-to-end by the CAS stream before the store's own identity check.
    ///
    /// <para>OPENING the object was already typed; COPYING it was not, and a provider that hands back bytes and then
    /// stops — a dropped connection, a revoked mount, an object removed under an open handle — is the routed lane's
    /// everyday failure. Only the verified-identity fault (<see cref="InvalidDataException"/>) had a verdict, so every
    /// other mid-copy fault escaped untyped and cost the reader the whole run instead of this one cell. Classified with
    /// the same table the rest of the plane consults, so a dead destination reads as BackendUnavailable rather than
    /// being flattened into "the stored copy does not match".</para>
    /// </summary>
    private async Task<byte[]> ReadRoutedAsync(RoutedRead read, CancellationToken cancellationToken)
    {
        var opened = await OpenRoutedAsync(read, cancellationToken).ConfigureAwait(false);

        await using var content = opened.Content;
        using var buffer = new MemoryStream();
        try
        {
            await content.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ArtifactReadFailureClassifier.TryClassify(ex, out var kind))
        {
            throw new ArtifactContentUnavailableException(read.ArtifactId, kind, ex);
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
            throw new ArtifactContentUnavailableException(read.ArtifactId, ArtifactContentUnavailableKind.PhysicalObjectMissing, detail: await MissingStampDetailAsync(read, cancellationToken).ConfigureAwait(false));

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
    /// When an object has NO Available location, the ledger usually still knows WHY — a demoted location carries its
    /// state, error code and observation time. Surfacing it is the difference between "missing, go dig with SQL" and
    /// "missing: the destination reported ObjectMissing on 2026-08-30". Best-effort: a failure to read the ledger
    /// never masks the original miss.
    /// </summary>
    private async Task<string?> MissingStampDetailAsync(RoutedRead read, CancellationToken cancellationToken)
    {
        try
        {
            var location = await _db.ArtifactLocation.AsNoTracking()
                .Where(l => l.TeamId == read.TeamId && l.ArtifactObjectId == read.ArtifactObjectId)
                .OrderByDescending(l => l.VerifiedAt)
                .Select(l => new { l.State, l.LastErrorCode, l.LastErrorMessage, l.VerifiedAt })
                .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);

            if (location is null) return "the object has no recorded location — the byte write never committed";

            return $"the location ledger records state {location.State}"
                 + (location.LastErrorCode is { Length: > 0 } code ? $", code {code}" : "")
                 + (location.LastErrorMessage is { Length: > 0 } message ? $": {message}" : "")
                 + (location.VerifiedAt is { } at ? $" (observed {at:u})" : "");
        }
        catch (Exception)
        {
            return null;   // the miss itself is already being thrown — never mask it with a diagnostics failure
        }
    }

    /// <summary>
    /// Every profile revision this object is durably recorded under, freshest observation first, through the seam every
    /// routed data class shares. The row itself stores only the object id, so this is "any Available location for these
    /// bytes", NOT "the one location the write stamped" — if a future replication or backfill adds a second location
    /// for the same object, this read follows the freshest of them. What routing state says TODAY is never consulted
    /// either way.
    /// </summary>
    private async Task<IReadOnlyList<RecordedStamp>> RecordedStampsAsync(RoutedRead read, CancellationToken cancellationToken)
    {
        var stamps = await RecordedArtifactLocations.AvailableFor(_db, read.TeamId)
            .Where(location => location.ArtifactObjectId == read.ArtifactObjectId)
            .OrderByDescending(location => location.VerifiedAt).ThenBy(location => location.LocationId)
            .Select(location => new RecordedStamp(location.StorageProfileId, location.StorageProfileRevision))
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

    /// <summary>One routed placement in flight: the write, the destination it resolved, and the payload every retry re-streams.</summary>
    private sealed record RoutedAttempt(OffloadedWrite Write, WorkflowArtifactDestination.Routed Routed, byte[] Payload);

    /// <summary>One routed read's coordinates: the tenant, the row asking, and the object it points at.</summary>
    private sealed record RoutedRead(Guid TeamId, Guid ArtifactId, Guid ArtifactObjectId);

    private sealed record RecordedStamp(Guid StorageProfileId, int StorageProfileRevision);
}
