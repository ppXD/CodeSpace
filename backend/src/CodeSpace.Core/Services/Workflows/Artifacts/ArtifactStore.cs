using System.Security.Cryptography;
using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Workflows.Artifacts.Exceptions;
using CodeSpace.Core.Services.Workflows.Artifacts.Retention;
using CodeSpace.Core.Services.Workflows.Artifacts.Runtime;
using Microsoft.EntityFrameworkCore;

namespace CodeSpace.Core.Services.Workflows.Artifacts;

/// <summary>
/// Content-addressable store backed by <c>workflow_artifact</c>. Per-team dedup via
/// <c>(team_id, sha256)</c> unique index; idempotent <see cref="PutAsync"/> returns the
/// existing id on duplicate.
///
/// Bytes up to <see cref="ArtifactStoreConfig.InlineThresholdBytes"/> live inline in the DB row; larger payloads
/// are offloaded out-of-band and the row keeps only a reference. Either way the metadata row (sha, size, content
/// type, tenant) is the durable source of truth.
///
/// <para>An offloaded payload goes wherever the team's ACTIVE <c>workflow-artifact/v1</c> storage route says (see the
/// <c>ArtifactStore.Routing</c> partial). With no route — the shipped state of every existing team — or with a route
/// that was created and never activated, that is the <see cref="IArtifactBlobBackend"/> and a <c>storage_url</c>,
/// unchanged. The threshold decision itself is untouched by routing: routing changes WHERE an offloaded blob goes,
/// never WHETHER it is offloaded.</para>
/// </summary>
public sealed partial class ArtifactStore : IArtifactStore, IArtifactRangeReader, IScopedDependency
{
    private readonly CodeSpaceDbContext _db;
    private readonly IArtifactBlobBackend _blobs;
    private readonly IWorkflowArtifactDestinationResolver _destinations;
    private readonly ArtifactRoutedPlane _routed;
    private readonly TimeProvider _clock;

    public ArtifactStore(CodeSpaceDbContext db, IArtifactBlobBackend blobs, IWorkflowArtifactDestinationResolver destinations, ArtifactRoutedPlane routed, TimeProvider clock)
    {
        _db = db;
        _blobs = blobs;
        _destinations = destinations;
        _routed = routed;
        _clock = clock;
    }

    public async Task<Guid> PutAsync(Guid teamId, ReadOnlyMemory<byte> bytes, string contentType, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(contentType))
            throw new ArgumentException("contentType is required.", nameof(contentType));

        return (await WriteAsync(new ArtifactWrite(teamId, bytes, contentType, null), cancellationToken).ConfigureAwait(false)).ArtifactId;
    }

    /// <summary>
    /// The one write path. Reports whether THIS call inserted the row, which is what
    /// <see cref="PutDeclaredAsync"/> needs: a retention declaration is only sound when it rides the insert, because
    /// nothing can dedup against a row that has not committed yet.
    /// </summary>
    private async Task<ArtifactRetentionWrite> WriteAsync(ArtifactWrite write, CancellationToken cancellationToken)
    {
        var (teamId, bytes, contentType, declaration) = write;
        var sha = ComputeSha256Hex(bytes.Span);

        // Idempotency: if (team, sha) already exists, return that id without an INSERT. The lookup is a unique-index
        // probe, so it is cheap and avoids racing the DB constraint on the common path.
        var existing = await FindDedupTargetAsync(teamId, sha, cancellationToken).ConfigureAwait(false);

        if (existing is not null)
        {
            // P2 slice 3 (a dedup hit verifies the bytes are still THERE): an offloaded blob under a wiped or
            // once-unconfigured root can be gone while the row's identity claim survives — trusting the key would
            // hand back an id whose read is doomed. We are HOLDING the exact bytes the claim describes (the sha
            // matched), so restore the blob instead of failing: self-healing beats a dead reference. Content
            // correctness on the healthy path stays the read's verification; inline rows have nothing to check.
            if (existing.StorageUrl is { } url && !await _blobs.ExistsAsync(url, cancellationToken).ConfigureAwait(false))
                await RestoreLocalBlobAsync(teamId, sha, bytes, cancellationToken).ConfigureAwait(false);

            return new ArtifactRetentionWrite(existing.Id, false);
        }

        // Size-routed storage: small payloads stay inline in the DB row; large ones are offloaded out-of-band
        // (content-addressed by sha, so the write is idempotent) and the row keeps only the reference. Exactly one of
        // inline_bytes / storage_url / cas_artifact_object_id is set (the table's CHECK enforces it).
        var offload = bytes.Length > ArtifactStoreConfig.InlineThresholdBytes;
        var placement = offload
            ? await PlaceOffloadedAsync(new OffloadedWrite(teamId, sha, bytes, contentType), cancellationToken).ConfigureAwait(false)
            : ArtifactPlacement.Inline;

        var artifact = new WorkflowArtifact
        {
            Id = Guid.NewGuid(),
            TeamId = teamId,
            Sha256 = sha,
            ContentType = contentType,
            SizeBytes = bytes.Length,
            InlineBytes = offload ? null : bytes.ToArray(),
            StorageUrl = placement.StorageUrl,
            CasArtifactObjectId = placement.CasArtifactObjectId,
            CreatedAt = DateTimeOffset.UtcNow,
        };

        _db.WorkflowArtifact.Add(artifact);

        // An INLINE or LOCAL-BLOB insert may be declared; a ROUTED one may not. The reaper can remove inline bytes with
        // the row and local-backend bytes through IArtifactBlobPurge, but nothing in this build removes routed bytes at
        // all (ArtifactRetentionDecision.RefuseUnpurgeable states what is still missing), so declaring one would only
        // mint a row that always settles as a terminal keep.
        var declared = declaration is not null && placement.CasArtifactObjectId is null ? DeclarationFor(declaration, artifact) : null;
        if (declared is not null) _db.WorkflowArtifactRetention.Add(declared);

        try
        {
            await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return new ArtifactRetentionWrite(artifact.Id, declared is not null);
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex))
        {
            // Race: another writer just inserted the same (team, sha). Re-query to return
            // their id. We don't re-throw because the contract is "PutAsync is idempotent
            // and ALWAYS returns a valid id for the given content".
            _db.Entry(artifact).State = EntityState.Detached;
            if (declared is not null) _db.Entry(declared).State = EntityState.Detached;

            var raceWinner = await FindDedupTargetAsync(teamId, sha, cancellationToken).ConfigureAwait(false);
            if (raceWinner == null)
                throw new ArtifactStorageDestinationUnavailableException(teamId, ArtifactCasProblemCode.TargetMissing);

            return new ArtifactRetentionWrite(raceWinner.Id, false);
        }
    }

    /// <summary>
    /// The dedup lookup, plus the two things a dedup hit owes the retention ledger. First it REVOKES any declaration on
    /// the row, because handing this id to a second producer means the ledger can no longer enumerate the artifact's
    /// references. Then it re-reads the row: the revoke serializes against a collector holding that ledger row, so a
    /// zero-row revoke can mean the collector just won — and returning a deleted row's id would hand back an id whose
    /// read is already doomed. A vanished row reads as "no dedup target" and the caller writes the bytes afresh.
    ///
    /// <para>Cost on the healthy path: one extra index probe and one UPDATE that matches no rows (so no tuple is
    /// written), and only on a dedup hit — a first write of new content pays nothing.</para>
    /// </summary>
    private async Task<ArtifactDedupTarget?> FindDedupTargetAsync(Guid teamId, string sha, CancellationToken cancellationToken)
    {
        var existing = await ReadDedupTargetAsync(teamId, sha, cancellationToken).ConfigureAwait(false);

        if (existing is null) return null;

        await RevokeDeclarationAsync(teamId, existing.Id, cancellationToken).ConfigureAwait(false);

        return await ReadDedupTargetAsync(teamId, sha, cancellationToken).ConfigureAwait(false);
    }

    private async Task<ArtifactDedupTarget?> ReadDedupTargetAsync(Guid teamId, string sha, CancellationToken cancellationToken) =>
        await _db.WorkflowArtifact.AsNoTracking()
            .Where(a => a.TeamId == teamId && a.Sha256 == sha
                && (a.CasArtifactObjectId == null || _db.ArtifactLocation.Any(location => location.TeamId == teamId
                    && location.ArtifactObjectId == a.CasArtifactObjectId && location.State == ArtifactLocationState.Available)))
            .Select(a => new ArtifactDedupTarget(a.Id, a.StorageUrl))
            .SingleOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

    private sealed record ArtifactDedupTarget(Guid Id, string? StorageUrl);

    /// <summary>One write's inputs. <c>Declaration</c> is null for every plain <see cref="PutAsync"/> caller, which is what keeps their bytes permanently unreapable.</summary>
    private sealed record ArtifactWrite(Guid TeamId, ReadOnlyMemory<byte> Bytes, string ContentType, ArtifactRetentionWriteRequest? Declaration);

    /// <summary>
    /// Puts a local row's missing blob back — but only while local disk is still where this team's new offloaded bytes
    /// belong. Once the team routes this data class, a restore would mint fresh local-disk bytes for a routed team,
    /// which is the same silent fallback the write path refuses; the dead reference then surfaces as a typed read
    /// failure instead of being papered over. Refusing here rather than throwing keeps the dedup contract intact:
    /// PutAsync still returns the existing id for content the store already knows.
    /// </summary>
    private async Task RestoreLocalBlobAsync(Guid teamId, string sha, ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken)
    {
        if (await _destinations.ResolveAsync(teamId, cancellationToken).ConfigureAwait(false) is not WorkflowArtifactDestination.Local) return;

        await _blobs.WriteAsync(sha, bytes, cancellationToken).ConfigureAwait(false);
    }

    public async Task<ArtifactBytes?> GetBytesAsync(Guid teamId, Guid artifactId, CancellationToken cancellationToken)
    {
        var row = await _db.WorkflowArtifact.AsNoTracking()
            .Where(a => a.Id == artifactId && a.TeamId == teamId)
            .SingleOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        if (row == null) return null;

        // Inline rows carry their bytes directly; offloaded rows resolve through whichever destination THEY recorded.
        var bytes = row.InlineBytes ?? await ReadOffloadedAsync(teamId, row, cancellationToken).ConfigureAwait(false);

        // P2 slice 2 (a read proves its bytes): the row's sha/size are the artifact's IDENTITY — the store's own
        // claims about the content. A blob that no longer matches (a corrupted/truncated file, a foreign write
        // under the content-addressed path, a size drift) must never flow silently into a prompt, a patch apply,
        // or an evidence read. Verified on EVERY read — inline rows included, so a mutated row can't lie either.
        if (bytes.Length != row.SizeBytes || !string.Equals(ComputeSha256Hex(bytes), row.Sha256, StringComparison.Ordinal))
            throw new InvalidOperationException(
                $"Artifact {artifactId} failed its read-back verification: stored claim sha256={row.Sha256} size={row.SizeBytes}, observed size={bytes.Length} — the underlying {(row.InlineBytes is null ? "blob" : "inline row")} no longer matches the store's identity claim; refusing to return unverified bytes.");

        return new ArtifactBytes
        {
            Id = row.Id,
            Sha256 = row.Sha256,
            ContentType = row.ContentType,
            Bytes = bytes,
        };
    }

    public async Task<ArtifactMetadata?> GetMetadataAsync(Guid teamId, Guid artifactId, CancellationToken cancellationToken)
    {
        var row = await _db.WorkflowArtifact.AsNoTracking()
            .Where(a => a.Id == artifactId && a.TeamId == teamId)
            .Select(a => new ArtifactMetadata
            {
                Id = a.Id,
                Sha256 = a.Sha256,
                ContentType = a.ContentType,
                SizeBytes = a.SizeBytes,
                CreatedAt = a.CreatedAt,
            })
            .SingleOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        return row;
    }

    public async Task<ArtifactRangeReadResult> ReadRangeAsync(Guid teamId, Guid artifactId, long offset, int length, CancellationToken cancellationToken)
    {
        if (offset < 0 || length <= 0)
            return ArtifactRangeReadResult.Failed(ArtifactRangeReadState.InvalidOffset);

        var row = await _db.WorkflowArtifact.AsNoTracking()
            .Where(a => a.Id == artifactId && a.TeamId == teamId)
            .Select(a => new { a.Sha256, a.ContentType, a.SizeBytes, a.InlineBytes, a.StorageUrl, a.CasArtifactObjectId })
            .SingleOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        if (row is null) return ArtifactRangeReadResult.Failed(ArtifactRangeReadState.MetadataMissing);
        if (offset > row.SizeBytes)
            return ArtifactRangeReadResult.Failed(ArtifactRangeReadState.InvalidOffset, row.SizeBytes, row.Sha256, row.ContentType);

        try
        {
            byte[] bytes;
            long observedLength;
            if (row.InlineBytes is { } inline)
            {
                observedLength = inline.LongLength;
                if (observedLength != row.SizeBytes)
                    return ArtifactRangeReadResult.Failed(ArtifactRangeReadState.IntegrityFailure, observedLength, row.Sha256, row.ContentType);
                if (offset > observedLength)
                    return ArtifactRangeReadResult.Failed(ArtifactRangeReadState.InvalidOffset, observedLength, row.Sha256, row.ContentType);

                var count = (int)Math.Min(length, observedLength - offset);
                bytes = inline.AsSpan((int)offset, count).ToArray();
            }
            else
            {
                var range = row.StorageUrl is { } url
                    ? await _blobs.ReadRangeAsync(url, offset, length, cancellationToken).ConfigureAwait(false)
                    : await ReadRoutedRangeAsync(RoutedReadFor(teamId, artifactId, row.CasArtifactObjectId), offset, length, cancellationToken).ConfigureAwait(false);
                bytes = range.Bytes;
                observedLength = range.TotalLength;
            }

            if (observedLength != row.SizeBytes)
                return ArtifactRangeReadResult.Failed(ArtifactRangeReadState.IntegrityFailure, observedLength, row.Sha256, row.ContentType);

            var fullObject = offset == 0 && bytes.LongLength == observedLength;
            if (fullObject && !string.Equals(ComputeSha256Hex(bytes), row.Sha256, StringComparison.Ordinal))
                return ArtifactRangeReadResult.Failed(ArtifactRangeReadState.IntegrityFailure, observedLength, row.Sha256, row.ContentType);

            return ArtifactRangeReadResult.Available(bytes, observedLength, row.Sha256, row.ContentType, fullObject);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (ArtifactContentUnavailableException ex)
        {
            // The routed path already classified the storage-plane fact; a bounded read reports it, never throws.
            return Unavailable(RangeStateOf(ex.Kind), row.SizeBytes, row.Sha256, row.ContentType);
        }
        catch (InvalidDataException)
        {
            // A verifying stream reports a truncated or digest-mismatched object this way, and InvalidDataException
            // derives from SystemException — the IOException arm below would never have caught it.
            return Unavailable(ArtifactRangeReadState.IntegrityFailure, row.SizeBytes, row.Sha256, row.ContentType);
        }
        catch (FileNotFoundException)
        {
            return Unavailable(ArtifactRangeReadState.PhysicalObjectMissing, row.SizeBytes, row.Sha256, row.ContentType);
        }
        catch (DirectoryNotFoundException)
        {
            return Unavailable(ArtifactRangeReadState.PhysicalObjectMissing, row.SizeBytes, row.Sha256, row.ContentType);
        }
        catch (UnauthorizedAccessException)
        {
            return Unavailable(ArtifactRangeReadState.AccessDenied, row.SizeBytes, row.Sha256, row.ContentType);
        }
        catch (InvalidOperationException)
        {
            return Unavailable(ArtifactRangeReadState.IntegrityFailure, row.SizeBytes, row.Sha256, row.ContentType);
        }
        catch (ArgumentOutOfRangeException)
        {
            return Unavailable(ArtifactRangeReadState.IntegrityFailure, row.SizeBytes, row.Sha256, row.ContentType);
        }
        catch (EndOfStreamException)
        {
            return Unavailable(ArtifactRangeReadState.IntegrityFailure, row.SizeBytes, row.Sha256, row.ContentType);
        }
        catch (IOException)
        {
            return Unavailable(ArtifactRangeReadState.BackendUnavailable, row.SizeBytes, row.Sha256, row.ContentType);
        }
    }

    private static ArtifactRangeReadResult Unavailable(ArtifactRangeReadState state, long totalLength, string sha256, string contentType) =>
        ArtifactRangeReadResult.Failed(state, totalLength, sha256, contentType);

    private static RoutedRead RoutedReadFor(Guid teamId, Guid artifactId, Guid? artifactObjectId) =>
        new(teamId, artifactId, artifactObjectId ?? throw new InvalidOperationException("Artifact storage locator is missing."));

    /// <summary>
    /// SHA-256 of <paramref name="bytes"/> as hex-lowercase. Deterministic, no salt — the
    /// digest IS the identity, callers that need authentication should pair it with a MAC
    /// at the producer side.
    /// </summary>
    public static string ComputeSha256Hex(ReadOnlySpan<byte> bytes)
    {
        Span<byte> digest = stackalloc byte[32];
        SHA256.HashData(bytes, digest);
        return Convert.ToHexString(digest).ToLowerInvariant();
    }

    private static bool IsUniqueViolation(DbUpdateException ex)
    {
        // Npgsql wraps PostgresException under DbUpdateException.InnerException. SQLSTATE
        // 23505 is unique_violation. We don't drag a Postgres-specific package into here —
        // duck-type by SQLSTATE on the inner exception.
        var inner = ex.InnerException;
        if (inner == null) return false;
        var sqlState = inner.GetType().GetProperty("SqlState")?.GetValue(inner) as string;
        return sqlState == "23505";
    }
}
