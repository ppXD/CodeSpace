using System.Buffers;
using System.Data;
using System.Security.Cryptography;
using System.Transactions;
using CodeSpace.Core.Services.Workflows.Artifacts.Exceptions;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;

namespace CodeSpace.Core.Services.Workflows.Artifacts;

public sealed partial class ArtifactStore : IArtifactContentVerifier
{
    public async Task<ArtifactVerifiedContent> VerifyAsync(ArtifactContentVerificationRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(request);
        ArgumentOutOfRangeException.ThrowIfNegative(request.SizeBytes);
        if (request.Sha256 is not { Length: 64 } || !request.Sha256.All(Uri.IsHexDigit)) throw new ArgumentException("A SHA-256 content identity is required.", nameof(request));
        try
        {
            var placement = await VerifyInlineOrReadPlacementAsync(request, cancellationToken).ConfigureAwait(false);
            if (!placement.InlineVerified)
            {
                var content = placement.StorageUrl is { } url
                    ? _blobs is IArtifactBlobStreamReader reader
                        ? await reader.OpenReadAsync(url, cancellationToken).ConfigureAwait(false)
                        : throw new ArtifactContentUnavailableException(request.ArtifactId, ArtifactContentUnavailableKind.BackendUnavailable, detail: "backend-streaming-read-unsupported")
                    : (await OpenRoutedAsync(new(request.TeamId, request.ArtifactId, placement.CasObjectId!.Value), cancellationToken).ConfigureAwait(false)).Content;
                await VerifyOwnedPhysicalStreamAsync(content, request, cancellationToken).ConfigureAwait(false);
            }
            cancellationToken.ThrowIfCancellationRequested();
            return new(request.TeamId, request.ArtifactId, request.Sha256.ToLowerInvariant(), request.SizeBytes);
        }
        catch (ArtifactContentUnavailableException) { throw; }
        catch (Exception ex) when (ArtifactReadFailureClassifier.TryClassify(ex, out var kind))
        {
            throw new ArtifactContentUnavailableException(request.ArtifactId, kind, ex);
        }
        catch (Exception ex) when (ex is NpgsqlException or TimeoutException)
        {
            throw new ArtifactContentUnavailableException(request.ArtifactId, ArtifactContentUnavailableKind.BackendUnavailable, ex);
        }
    }

    /// <summary>Read current committed metadata independently of a caller's tracked state or old transaction snapshot. Inline bytea stays on the sequential reader: even an oversized legacy row cannot allocate its whole payload. The caller's transaction is never committed, rolled back or cleared here.</summary>
    private async Task<VerificationPlacement> VerifyInlineOrReadPlacementAsync(ArtifactContentVerificationRequest request, CancellationToken cancellationToken)
    {
        using var ambient = new TransactionScope(TransactionScopeOption.Suppress, TransactionScopeAsyncFlowOption.Enabled);
        await using var connection = (NpgsqlConnection)((ICloneable)_db.Database.GetDbConnection()).Clone();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT sha256, size_bytes, storage_url, cas_artifact_object_id, inline_bytes FROM workflow_artifact WHERE id = @artifact_id AND team_id = @team_id";
        command.Parameters.AddWithValue("artifact_id", NpgsqlDbType.Uuid, request.ArtifactId);
        command.Parameters.AddWithValue("team_id", NpgsqlDbType.Uuid, request.TeamId);
        await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess | CommandBehavior.SingleRow, cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) throw new ArtifactContentUnavailableException(request.ArtifactId, ArtifactContentUnavailableKind.MetadataMissing);
        var sha256 = reader.GetString(0);
        var size = reader.GetInt64(1);
        if (size != request.SizeBytes || !string.Equals(sha256, request.Sha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("The team-visible artifact metadata does not match the expected content identity.");
        var url = reader.IsDBNull(2) ? null : reader.GetString(2);
        var objectId = reader.IsDBNull(3) ? (Guid?)null : reader.GetGuid(3);
        var inline = !await reader.IsDBNullAsync(4, cancellationToken).ConfigureAwait(false);
        var destinations = (url is null ? 0 : 1) + (objectId is null ? 0 : 1) + (inline ? 1 : 0);
        if (destinations == 0) throw NoDestinationRecorded(request.ArtifactId);
        if (destinations != 1) throw new InvalidDataException("The artifact records conflicting content destinations.");
        if (inline)
        {
            await VerifyOwnedPhysicalStreamAsync(reader.GetStream(4), request, cancellationToken).ConfigureAwait(false);
        }
        return new(url, objectId, inline);
    }

    /// <summary>Own every opened stream through completion. Cleanup failures cannot replace a cancellation or the original integrity verdict; the secondary diagnostic remains on that original exception.</summary>
    private static async Task VerifyOwnedPhysicalStreamAsync(Stream content, ArtifactContentVerificationRequest request, CancellationToken cancellationToken)
    {
        Exception? primary = null;
        try { await VerifyPhysicalStreamAsync(content, request, cancellationToken).ConfigureAwait(false); }
        catch (Exception ex) { primary = ex; throw; }
        finally
        {
            try { await content.DisposeAsync().ConfigureAwait(false); }
            catch (Exception cleanup) when (primary != null && cleanup is not OutOfMemoryException and not AccessViolationException)
            {
                primary.Data["ArtifactContentReadCleanupFailure"] = cleanup;
            }
        }
    }

    /// <summary>Consume exactly the claimed length plus one EOF probe with bounded memory. Never trust Stream.Length or success after only a matching prefix; an overlong source stops after its first excess byte.</summary>
    private static async Task VerifyPhysicalStreamAsync(Stream content, ArtifactContentVerificationRequest request, CancellationToken cancellationToken)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = ArrayPool<byte>.Shared.Rent(StreamingBufferBytes);
        try
        {
            var remaining = request.SizeBytes;
            while (remaining > 0)
            {
                var read = await content.ReadAsync(buffer.AsMemory(0, (int)Math.Min(remaining, StreamingBufferBytes)), cancellationToken).ConfigureAwait(false);
                if (read == 0) throw new InvalidDataException("Artifact content ended before its recorded size.");
                hash.AppendData(buffer, 0, read);
                remaining -= read;
            }
            if (await content.ReadAsync(buffer.AsMemory(0, 1), cancellationToken).ConfigureAwait(false) != 0) throw new InvalidDataException("Artifact content exceeds its recorded size.");
            if (!CryptographicOperations.FixedTimeEquals(hash.GetHashAndReset(), Convert.FromHexString(request.Sha256))) throw new InvalidDataException("Artifact content does not match its recorded SHA-256.");
        }
        finally { ArrayPool<byte>.Shared.Return(buffer, clearArray: true); }
    }

    private sealed record VerificationPlacement(string? StorageUrl, Guid? CasObjectId, bool InlineVerified);
}
