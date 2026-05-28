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
/// Inline storage only — bytes over <see cref="ArtifactStoreConfig.InlineThresholdBytes"/>
/// raise an explicit exception so callers fail loudly rather than silently truncating.
/// </summary>
public sealed class ArtifactStore : IArtifactStore, IScopedDependency
{
    private readonly CodeSpaceDbContext _db;

    public ArtifactStore(CodeSpaceDbContext db) { _db = db; }

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

        var threshold = ArtifactStoreConfig.InlineThresholdBytes;
        if (bytes.Length > threshold)
        {
            // Storage-URL path isn't wired yet. Reject explicitly so callers know they need
            // a different storage backend rather than silently dropping bytes.
            throw new InvalidOperationException(
                $"Artifact size {bytes.Length} exceeds the inline threshold {threshold} bytes. " +
                $"Set {ArtifactStoreConfig.InlineThresholdEnvVar} to raise the limit (capped at PostgreSQL's bytea max), " +
                "or wait for the storage_url backend.");
        }

        var artifact = new WorkflowArtifact
        {
            Id = Guid.NewGuid(),
            TeamId = teamId,
            Sha256 = sha,
            ContentType = contentType,
            SizeBytes = bytes.Length,
            InlineBytes = bytes.ToArray(),
            StorageUrl = null,
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

        // Inline only — PutAsync rejects oversize content so every row has bytes.
        if (row.InlineBytes == null)
            throw new InvalidOperationException(
                $"Artifact {artifactId} has no inline bytes (storage_url={row.StorageUrl}); " +
                "out-of-band storage backend not wired.");

        return new ArtifactBytes
        {
            Id = row.Id,
            Sha256 = row.Sha256,
            ContentType = row.ContentType,
            Bytes = row.InlineBytes,
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
