using System.Security.Cryptography;
using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace CodeSpace.Core.Services.Workflows.Artifacts;

/// <summary>
/// Content-addressable store backed by <c>workflow_artifact</c>. Per-team dedup via
/// <c>(team_id, sha256)</c> unique index; idempotent <see cref="PutAsync"/> returns the
/// existing id on duplicate.
///
/// Bytes up to <see cref="ArtifactStoreConfig.InlineThresholdBytes"/> live inline in the DB row; larger payloads
/// are offloaded to the <see cref="IArtifactBlobBackend"/> (out-of-band) and the row keeps only a
/// <c>storage_url</c> reference. Either way the metadata row (sha, size, content type, tenant) is the durable
/// source of truth.
/// </summary>
public sealed class ArtifactStore : IArtifactStore, IScopedDependency
{
    private readonly CodeSpaceDbContext _db;
    private readonly IArtifactBlobBackend _blobs;

    public ArtifactStore(CodeSpaceDbContext db, IArtifactBlobBackend blobs)
    {
        _db = db;
        _blobs = blobs;
    }

    public async Task<Guid> PutAsync(Guid teamId, ReadOnlyMemory<byte> bytes, string contentType, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(contentType))
            throw new ArgumentException("contentType is required.", nameof(contentType));

        var sha = ComputeSha256Hex(bytes.Span);

        // Idempotency: if (team, sha) already exists, return that id without an INSERT.
        // The query is cheap (unique index lookup) and avoids racing the DB constraint.
        var existing = await _db.WorkflowArtifact.AsNoTracking()
            .Where(a => a.TeamId == teamId && a.Sha256 == sha)
            .Select(a => (Guid?)a.Id)
            .SingleOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        if (existing.HasValue) return existing.Value;

        // Size-routed storage: small payloads stay inline in the DB row; large ones are offloaded to the
        // out-of-band backend (content-addressed by sha, so the write is idempotent) and the row keeps only the
        // storage_url. Exactly one of inline_bytes / storage_url is set (the table's CHECK enforces it).
        var offload = bytes.Length > ArtifactStoreConfig.InlineThresholdBytes;
        var storageUrl = offload ? await _blobs.WriteAsync(sha, bytes, cancellationToken).ConfigureAwait(false) : null;

        var artifact = new WorkflowArtifact
        {
            Id = Guid.NewGuid(),
            TeamId = teamId,
            Sha256 = sha,
            ContentType = contentType,
            SizeBytes = bytes.Length,
            InlineBytes = offload ? null : bytes.ToArray(),
            StorageUrl = storageUrl,
            CreatedAt = DateTimeOffset.UtcNow,
        };

        _db.WorkflowArtifact.Add(artifact);

        try
        {
            await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return artifact.Id;
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex))
        {
            // Race: another writer just inserted the same (team, sha). Re-query to return
            // their id. We don't re-throw because the contract is "PutAsync is idempotent
            // and ALWAYS returns a valid id for the given content".
            _db.Entry(artifact).State = EntityState.Detached;

            var raceWinner = await _db.WorkflowArtifact.AsNoTracking()
                .Where(a => a.TeamId == teamId && a.Sha256 == sha)
                .Select(a => a.Id)
                .SingleAsync(cancellationToken)
                .ConfigureAwait(false);

            return raceWinner;
        }
    }

    public async Task<ArtifactBytes?> GetBytesAsync(Guid teamId, Guid artifactId, CancellationToken cancellationToken)
    {
        var row = await _db.WorkflowArtifact.AsNoTracking()
            .Where(a => a.Id == artifactId && a.TeamId == teamId)
            .SingleOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        if (row == null) return null;

        // Inline rows carry their bytes directly; offloaded rows resolve their storage_url through the backend.
        var bytes = row.InlineBytes
            ?? await _blobs.ReadAsync(
                row.StorageUrl ?? throw new InvalidOperationException($"Artifact {artifactId} has neither inline bytes nor a storage_url."),
                cancellationToken).ConfigureAwait(false);

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
