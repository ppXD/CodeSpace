using System.Buffers;
using System.Security.Cryptography;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Workflows.Artifacts.Exceptions;
using CodeSpace.Core.Services.Workflows.Artifacts.Routing;
using CodeSpace.Core.Services.Workflows.Artifacts.Runtime;
using CodeSpace.Messages.Constants;
using Microsoft.EntityFrameworkCore;

namespace CodeSpace.Core.Services.Workflows.Artifacts;

/// <summary>
/// Length-known, re-readable streaming writes. Identity admission is a bounded first pass. Small content is retained
/// only up to the configured inline threshold; large content is reopened for the selected local or routed writer.
/// </summary>
public sealed partial class ArtifactStore
{
    private const int StreamingBufferBytes = 128 * 1024;

    public async Task<Guid> PutAsync(ArtifactStreamWriteRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Source);
        if (request.TeamId == Guid.Empty) throw new ArgumentException("TeamId is required.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.ContentType)) throw new ArgumentException("ContentType is required.", nameof(request));
        ArgumentOutOfRangeException.ThrowIfNegative(request.Source.LengthBytes);

        var admission = await AdmitAsync(request.Source, cancellationToken).ConfigureAwait(false);
        var existing = await FindDedupTargetAsync(request.TeamId, admission.Sha256, cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            if (existing.StorageUrl is { } url && !await _blobs.ExistsAsync(url, cancellationToken).ConfigureAwait(false))
                await RestoreLocalStreamAsync(request, admission.Sha256, cancellationToken).ConfigureAwait(false);
            return existing.Id;
        }

        var placement = admission.InlineBytes is null
            ? await PlaceOffloadedStreamAsync(request, admission.Sha256, cancellationToken).ConfigureAwait(false)
            : ArtifactPlacement.Inline;
        var artifact = new WorkflowArtifact
        {
            Id = Guid.NewGuid(),
            TeamId = request.TeamId,
            Sha256 = admission.Sha256,
            ContentType = request.ContentType,
            SizeBytes = request.Source.LengthBytes,
            InlineBytes = admission.InlineBytes,
            StorageUrl = placement.StorageUrl,
            CasArtifactObjectId = placement.CasArtifactObjectId,
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
            _db.Entry(artifact).State = EntityState.Detached;
            var raceWinner = await FindDedupTargetAsync(request.TeamId, admission.Sha256, cancellationToken).ConfigureAwait(false);
            if (raceWinner is null)
                throw new ArtifactStorageDestinationUnavailableException(request.TeamId, ArtifactCasProblemCode.TargetMissing);
            return raceWinner.Id;
        }
    }

    private static async Task<StreamAdmission> AdmitAsync(IArtifactWriteSource source, CancellationToken cancellationToken)
    {
        var expectedLength = source.LengthBytes;
        var inline = expectedLength <= ArtifactStoreConfig.InlineThresholdBytes ? new byte[checked((int)expectedLength)] : null;
        var buffer = ArrayPool<byte>.Shared.Rent(StreamingBufferBytes);
        try
        {
            using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using var content = await OpenSourceAsync(source, cancellationToken).ConfigureAwait(false);
            long observed = 0;
            while (true)
            {
                var read = await content.ReadAsync(buffer.AsMemory(0, StreamingBufferBytes), cancellationToken).ConfigureAwait(false);
                if (read == 0) break;
                var next = checked(observed + read);
                if (next > expectedLength) throw LengthMismatch(expectedLength, next);
                digest.AppendData(buffer, 0, read);
                if (inline is not null) Buffer.BlockCopy(buffer, 0, inline, checked((int)observed), read);
                observed = next;
            }

            if (observed != expectedLength) throw LengthMismatch(expectedLength, observed);
            return new StreamAdmission(Convert.ToHexStringLower(digest.GetHashAndReset()), inline);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private async Task RestoreLocalStreamAsync(ArtifactStreamWriteRequest request, string sha256, CancellationToken cancellationToken)
    {
        if (await _destinations.ResolveAsync(request.TeamId, cancellationToken).ConfigureAwait(false) is not WorkflowArtifactDestination.Local) return;
        await WriteLegacyStreamAsync(request.Source, sha256, cancellationToken).ConfigureAwait(false);
    }

    private async Task<ArtifactPlacement> PlaceOffloadedStreamAsync(ArtifactStreamWriteRequest request, string sha256, CancellationToken cancellationToken)
    {
        var destination = await _destinations.ResolveAsync(request.TeamId, cancellationToken).ConfigureAwait(false);
        return destination switch
        {
            WorkflowArtifactDestination.Local => ArtifactPlacement.Local(await WriteLegacyStreamAsync(request.Source, sha256, cancellationToken).ConfigureAwait(false)),
            WorkflowArtifactDestination.Routed routed => ArtifactPlacement.Routed(await TransferStreamAsync(request, sha256, routed, cancellationToken).ConfigureAwait(false)),
            WorkflowArtifactDestination.Unusable unusable => throw new ArtifactStorageDestinationUnavailableException(request.TeamId, unusable.Problem),
            _ => throw new ArtifactStorageDestinationUnavailableException(request.TeamId, WorkflowArtifactDestinationProblem.ResolutionFailed),
        };
    }

    private async Task<string> WriteLegacyStreamAsync(IArtifactWriteSource source, string sha256, CancellationToken cancellationToken)
    {
        if (_blobs is not IArtifactBlobStreamWriter writer) throw new ArtifactStreamingWriteUnavailableException(_blobs.GetType());
        await using var content = await OpenSourceAsync(source, cancellationToken).ConfigureAwait(false);
        return await writer.WriteStreamAsync(sha256, content, source.LengthBytes, cancellationToken).ConfigureAwait(false);
    }

    private async Task<Guid> TransferStreamAsync(ArtifactStreamWriteRequest request, string sha256, WorkflowArtifactDestination.Routed routed, CancellationToken cancellationToken)
    {
        var deadline = _clock.GetUtcNow() + RoutedWaitBudget;
        var backoff = RoutedPollFloor;
        while (true)
        {
            var transfer = await PutStreamOnceAsync(request, sha256, routed, cancellationToken).ConfigureAwait(false);
            if (transfer is ArtifactCasTransferResult.Committed committed) return committed.ArtifactObjectId;
            if (transfer is not ArtifactCasTransferResult.Deferred || _clock.GetUtcNow() >= deadline)
                throw new ArtifactStorageDestinationUnavailableException(request.TeamId, ProblemOf(transfer));
            await Task.Delay(backoff, _clock, cancellationToken).ConfigureAwait(false);
            backoff = backoff < RoutedPollCeiling ? backoff + backoff : RoutedPollCeiling;
        }
    }

    private async Task<ArtifactCasTransferResult> PutStreamOnceAsync(ArtifactStreamWriteRequest request, string sha256, WorkflowArtifactDestination.Routed routed, CancellationToken cancellationToken)
    {
        await using var content = await OpenSourceAsync(request.Source, cancellationToken).ConfigureAwait(false);
        return await _routed.Transfers.PutAsync(new ArtifactCasTransferRequest
        {
            TeamId = request.TeamId, StorageProfileId = routed.StorageProfileId, StorageProfileRevision = routed.StorageProfileRevision,
            IdempotencyScope = IdempotencyScopeFor(sha256), TargetObjectKey = ObjectKeyFor(sha256),
            Content = content, ExpectedSizeBytes = request.Source.LengthBytes, ExpectedSha256 = sha256,
            ContentType = request.ContentType, ActorId = SystemUsers.SeederId,
        }, cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask<Stream> OpenSourceAsync(IArtifactWriteSource source, CancellationToken cancellationToken)
    {
        var content = await source.OpenReadAsync(cancellationToken).ConfigureAwait(false);
        if (content is null) throw new InvalidDataException("Artifact source returned no stream.");
        if (!content.CanRead)
        {
            await content.DisposeAsync().ConfigureAwait(false);
            throw new InvalidDataException("Artifact source returned a stream that is not readable.");
        }
        return content;
    }

    private static InvalidDataException LengthMismatch(long expected, long observed) =>
        new($"Artifact source length mismatch: expected {expected} bytes, observed {observed}.");

    private sealed record StreamAdmission(string Sha256, byte[]? InlineBytes);
}
