using System.Buffers;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Workflows.Artifacts.Routing;
using CodeSpace.Core.Services.Workflows.Artifacts.Runtime;
using CodeSpace.Messages.Dtos.Agents;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace CodeSpace.Core.Services.Agents.AgentRunLogging;

public sealed partial class AgentRunLogService
{
    internal const int VerificationSegmentsPerStep = 8;
    internal const int VerificationLocationsPerPage = 4;

    private async Task<AgentRunLogCompleteResult> CompleteManifestAsync(AgentRunLogCompleteRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var prepared = await MutateVerificationAsync(new VerificationMutation(request), cancellationToken).ConfigureAwait(false);
            if (prepared.Problem != null) return new AgentRunLogCompleteResult.Rejected(prepared.Problem);
            if (prepared.Sealed) return new AgentRunLogCompleteResult.Completed(prepared.Metadata!);
            var checkpoint = prepared.Verification!;
            await using var db = CreateDb();
            var entries = await (from segment in db.AgentRunLogSegment.AsNoTracking()
                                 join artifact in db.ArtifactObject.AsNoTracking() on new { segment.TeamId, Id = segment.ArtifactObjectId } equals new { artifact.TeamId, artifact.Id }
                                 where segment.TeamId == request.TeamId && segment.StreamId == request.StreamId
                                     && segment.SegmentOrdinal >= checkpoint.NextSegmentOrdinal && segment.SegmentOrdinal <= checkpoint.SegmentCount
                                 orderby segment.SegmentOrdinal
                                 select new AgentRunLogManifestEntry(segment.SegmentOrdinal, segment.StartOffsetBytes, segment.LengthBytes, segment.ArtifactObjectId, artifact.Digest))
                .Take(VerificationSegmentsPerStep).ToListAsync(cancellationToken).ConfigureAwait(false);
            if (entries.Count == 0 && checkpoint.NextSegmentOrdinal <= checkpoint.SegmentCount) return RejectComplete(AgentRunLogProblemCode.ArtifactMissing);
            foreach (var entry in entries)
            {
                if (entry.Ordinal != checkpoint.NextSegmentOrdinal || entry.Offset != checkpoint.VerifiedBytes) return RejectComplete(AgentRunLogProblemCode.ArtifactCorrupt);
                var problem = await VerifyManifestPartAsync(request, entry, cancellationToken).ConfigureAwait(false);
                if (problem != null) return new AgentRunLogCompleteResult.Rejected(problem);
                var advanced = await MutateVerificationAsync(new VerificationMutation(request, checkpoint.Revision, entry), cancellationToken).ConfigureAwait(false);
                if (advanced.Problem != null) return new AgentRunLogCompleteResult.Rejected(advanced.Problem);
                checkpoint = advanced.Verification!;
            }
            if (checkpoint.NextSegmentOrdinal > checkpoint.SegmentCount)
            {
                var sealedResult = await MutateVerificationAsync(new VerificationMutation(request, checkpoint.Revision, Seal: true), cancellationToken).ConfigureAwait(false);
                return sealedResult.Problem != null ? new AgentRunLogCompleteResult.Rejected(sealedResult.Problem) : new AgentRunLogCompleteResult.Completed(sealedResult.Metadata!);
            }
            return new AgentRunLogCompleteResult.Progress(prepared.Metadata!, checkpoint.NextSegmentOrdinal - 1, checkpoint.VerifiedBytes);
        }
        catch (DbUpdateException exception) when (exception.InnerException is PostgresException { SqlState: "P0111" }) { return RejectComplete(AgentRunLogProblemCode.StaleWorker); }
        catch (DbUpdateException exception) when (exception.InnerException is PostgresException { SqlState: "P0112" }) { return RejectComplete(AgentRunLogProblemCode.CaptureClaimConflict); }
        catch (DbUpdateException exception) when (exception.InnerException is PostgresException { SqlState: "P0113" }) { return RejectComplete(AgentRunLogProblemCode.StaleRecoveryClaim); }
        catch (DbUpdateConcurrencyException) { return RejectComplete(AgentRunLogProblemCode.ConcurrentMutation, true); }
    }

    private async Task<VerificationMutationResult> MutateVerificationAsync(VerificationMutation mutation, CancellationToken cancellationToken)
    {
        var request = mutation.Request;
        await using var db = CreateDb();
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var run = await db.AgentRun.FromSqlInterpolated($"SELECT agent_run.*, xmin FROM agent_run WHERE team_id = {request.TeamId} AND id = {request.AgentRunId} FOR UPDATE").SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (run == null) return VerificationRejected(AgentRunLogProblemCode.Missing);
        if (run.FenceEpoch != request.WorkerFenceEpoch) return VerificationRejected(AgentRunLogProblemCode.StaleWorker);
        var stream = await db.AgentRunLogStream.FromSqlInterpolated($"SELECT agent_run_log_stream.*, xmin FROM agent_run_log_stream WHERE team_id = {request.TeamId} AND id = {request.StreamId} AND agent_run_id = {request.AgentRunId} FOR UPDATE").SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (stream == null) return VerificationRejected(AgentRunLogProblemCode.Missing);
        if (stream.SchemaVersion != 3) return VerificationRejected(AgentRunLogProblemCode.Unsupported);
        if (stream.WorkerFenceEpoch != request.WorkerFenceEpoch || stream.CaptureSessionId != request.CaptureSessionId) return VerificationRejected(AgentRunLogProblemCode.CaptureClaimConflict);
        if (stream.CaptureFinalizedAt == null) return VerificationRejected(AgentRunLogProblemCode.SourceNotFinalized);
        var checkpoint = await db.AgentRunLogVerification.SingleOrDefaultAsync(value => value.TeamId == request.TeamId && value.StreamId == request.StreamId && value.StreamRevision == request.ExpectedRevision, cancellationToken).ConfigureAwait(false);
        if (stream.State == AgentRunLogStreamState.Completed && checkpoint is { SealedAt: not null }
            && checkpoint.WorkerFenceEpoch == request.WorkerFenceEpoch && checkpoint.CaptureSessionId == request.CaptureSessionId
            && stream.Revision == checkpoint.StreamRevision + 1 && stream.ManifestDigest != null && stream.ManifestDigest.AsSpan().SequenceEqual(checkpoint.ManifestDigest))
            return new VerificationMutationResult(checkpoint, Project(stream), null, true);
        if (stream.State != AgentRunLogStreamState.Open) return VerificationRejected(AgentRunLogProblemCode.StreamTerminal);
        if (stream.Revision != request.ExpectedRevision) return VerificationRejected(AgentRunLogProblemCode.ConcurrentMutation, true);
        if (!await ValidateVerificationRecoveryAsync(db, request, stream, cancellationToken).ConfigureAwait(false)) return VerificationRejected(AgentRunLogProblemCode.StaleRecoveryClaim);
        var now = await db.Database.SqlQueryRaw<DateTimeOffset>("SELECT clock_timestamp() AS \"Value\"").SingleAsync(cancellationToken).ConfigureAwait(false);
        if (checkpoint == null)
        {
            if (mutation.ExpectedCheckpointRevision != null) return VerificationRejected(AgentRunLogProblemCode.ConcurrentMutation, true);
            checkpoint = new AgentRunLogVerification
            {
                Id = Guid.NewGuid(), TeamId = request.TeamId, AgentRunId = request.AgentRunId, StreamId = request.StreamId,
                WorkerFenceEpoch = request.WorkerFenceEpoch, CaptureSessionId = request.CaptureSessionId, StreamRevision = request.ExpectedRevision,
                SegmentCount = stream.SegmentCount, TotalBytes = stream.TotalBytes, SourceOffsetBytes = stream.SourceOffsetBytes,
                CreatedAt = now, LastModifiedAt = now,
            };
            checkpoint.Accumulator = AgentRunLogManifestDigest.Begin(checkpoint);
            ApplyVerificationClaim(checkpoint, request.RecoveryClaim);
            db.AgentRunLogVerification.Add(checkpoint);
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        else if (checkpoint.WorkerFenceEpoch != request.WorkerFenceEpoch || checkpoint.CaptureSessionId != request.CaptureSessionId
            || checkpoint.SegmentCount != stream.SegmentCount || checkpoint.TotalBytes != stream.TotalBytes || checkpoint.SourceOffsetBytes != stream.SourceOffsetBytes)
            return VerificationRejected(AgentRunLogProblemCode.CaptureClaimConflict);
        if (mutation.ExpectedCheckpointRevision != null)
        {
            if (checkpoint.Revision != mutation.ExpectedCheckpointRevision) return VerificationRejected(AgentRunLogProblemCode.ConcurrentMutation, true);
            ApplyVerificationClaim(checkpoint, request.RecoveryClaim);
            if (mutation.Entry is { } entry)
            {
                if (checkpoint.NextSegmentOrdinal != entry.Ordinal || checkpoint.VerifiedBytes != entry.Offset) return VerificationRejected(AgentRunLogProblemCode.ConcurrentMutation, true);
                checkpoint.Accumulator = AgentRunLogManifestDigest.Append(checkpoint.Accumulator, entry);
                checkpoint.NextSegmentOrdinal++;
                checkpoint.VerifiedBytes += entry.Length;
            }
            else if (mutation.Seal)
            {
                if (checkpoint.NextSegmentOrdinal != checkpoint.SegmentCount + 1 || checkpoint.VerifiedBytes != checkpoint.TotalBytes) return VerificationRejected(AgentRunLogProblemCode.ArtifactCorrupt);
                checkpoint.ManifestDigest = AgentRunLogManifestDigest.Seal(checkpoint);
                checkpoint.SealedAt = now;
            }
            checkpoint.Revision++;
            checkpoint.LastModifiedAt = now;
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            if (mutation.Seal)
            {
                stream.ManifestDigest = checkpoint.ManifestDigest;
                stream.State = AgentRunLogStreamState.Completed;
                stream.Revision++;
                stream.CompletedAt = now;
                stream.LastModifiedAt = now;
                await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new VerificationMutationResult(checkpoint, Project(stream), null, mutation.Seal);
    }

    private static async Task<bool> ValidateVerificationRecoveryAsync(CodeSpaceDbContext db, AgentRunLogCompleteRequest request, AgentRunLogStream stream, CancellationToken cancellationToken)
    {
        if (request.RecoveryClaim is not { } claim) return true;
        var intent = await db.AgentRunLogCaptureIntent.FromSqlInterpolated($"SELECT agent_run_log_capture_intent.*, xmin FROM agent_run_log_capture_intent WHERE team_id = {request.TeamId} AND id = {claim.IntentId} FOR UPDATE").SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        var now = await db.Database.SqlQueryRaw<DateTimeOffset>("SELECT clock_timestamp() AS \"Value\"").SingleAsync(cancellationToken).ConfigureAwait(false);
        return intent != null && intent.AgentRunId == request.AgentRunId && intent.WorkerFenceEpoch == request.WorkerFenceEpoch && intent.CaptureSessionId == request.CaptureSessionId
            && intent.StreamKind == stream.StreamKind && intent.ContentType == stream.ContentType && intent.ContentEncoding == stream.ContentEncoding && intent.CaptureSource == stream.CaptureSource
            && (intent.StreamId == null || intent.StreamId == stream.Id) && intent.State is AgentRunLogCaptureIntentState.Expected or AgentRunLogCaptureIntentState.Opened or AgentRunLogCaptureIntentState.SourceFinalized
            && intent.RecoveryOwnerId == claim.OwnerId && intent.RecoveryFenceEpoch == claim.FenceEpoch && intent.RecoveryLeaseExpiresAt > now;
    }

    private async Task<AgentRunLogProblem?> VerifyManifestPartAsync(AgentRunLogCompleteRequest request, AgentRunLogManifestEntry entry, CancellationToken cancellationToken)
    {
        DateTimeOffset? cursorTime = null;
        Guid? cursorId = null;
        AgentRunLogProblem? firstProblem = null;
        AgentRunLogProblem? retryableProblem = null;
        while (true)
        {
            await using var db = CreateDb();
            var query = RecordedArtifactLocations.AvailableFor(db, request.TeamId).Where(value => value.ArtifactObjectId == entry.ArtifactObjectId && value.VerifiedAt != null);
            if (cursorId != null) query = query.Where(value => value.VerifiedAt < cursorTime || value.VerifiedAt == cursorTime && value.LocationId.CompareTo(cursorId.Value) > 0);
            var locations = await query.OrderByDescending(value => value.VerifiedAt).ThenBy(value => value.LocationId).Take(VerificationLocationsPerPage).ToListAsync(cancellationToken).ConfigureAwait(false);
            foreach (var location in locations)
            {
                var opened = await _artifacts.OpenReadAsync(new ArtifactCasReadRequest { TeamId = request.TeamId, ArtifactObjectId = entry.ArtifactObjectId, StorageProfileId = location.StorageProfileId, StorageProfileRevision = location.StorageProfileRevision, OperationTimeout = request.OperationTimeout }, cancellationToken).ConfigureAwait(false);
                AgentRunLogProblem? problem;
                if (opened is ArtifactCasReadResult.Opened available) problem = await VerifyOwnedManifestPartAsync(available, entry, cancellationToken).ConfigureAwait(false);
                else problem = opened is ArtifactCasReadResult.Unavailable unavailable ? Map(unavailable.Problem) : Problem(AgentRunLogProblemCode.BackendUnavailable, true);
                if (problem == null) return null;
                firstProblem ??= problem;
                if (problem.IsTransient) retryableProblem ??= problem;
            }
            if (locations.Count < VerificationLocationsPerPage) return retryableProblem ?? firstProblem ?? Problem(AgentRunLogProblemCode.ArtifactMissing);
            cursorTime = locations[^1].VerifiedAt;
            cursorId = locations[^1].LocationId;
        }
    }

    private static async Task<AgentRunLogProblem?> VerifyOwnedManifestPartAsync(ArtifactCasReadResult.Opened opened, AgentRunLogManifestEntry entry, CancellationToken cancellationToken)
    {
        Exception? primary = null;
        try
        {
            try
            {
                if (opened.SizeBytes != entry.Length || !string.Equals(opened.Sha256, Convert.ToHexStringLower(entry.Digest), StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("The recorded log segment identity changed.");
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                var buffer = ArrayPool<byte>.Shared.Rent(CopyBufferBytes);
                long observed = 0;
                try
                {
                    while (true)
                    {
                        var read = await opened.Content.ReadAsync(buffer.AsMemory(0, CopyBufferBytes), cancellationToken).ConfigureAwait(false);
                        if (read == 0) break;
                        observed += read;
                        if (observed > entry.Length) throw new InvalidDataException("Log segment exceeded its recorded length.");
                        hash.AppendData(buffer, 0, read);
                    }
                    if (observed != entry.Length || !CryptographicOperations.FixedTimeEquals(hash.GetHashAndReset(), entry.Digest)) throw new InvalidDataException("Log segment failed full SHA-256/length verification.");
                }
                finally { ArrayPool<byte>.Shared.Return(buffer); }
            }
            catch (Exception exception) { primary = exception; }
            try { await opened.Content.DisposeAsync().ConfigureAwait(false); }
            catch (Exception exception) when (primary != null) { primary.Data["LogVerificationCleanupFailure"] = exception; }
            if (primary != null) ExceptionDispatchInfo.Capture(primary).Throw();
            return null;
        }
        catch (InvalidDataException) { return Problem(AgentRunLogProblemCode.ArtifactCorrupt); }
        catch (UnauthorizedAccessException) { return Problem(AgentRunLogProblemCode.AccessDenied); }
        catch (IOException) { return Problem(AgentRunLogProblemCode.BackendUnavailable, true); }
    }

    private static void ApplyVerificationClaim(AgentRunLogVerification verification, AgentRunLogRecoveryClaimRef? claim)
    {
        verification.RecoveryIntentId = claim?.IntentId;
        verification.RecoveryOwnerId = claim?.OwnerId;
        verification.RecoveryFenceEpoch = claim?.FenceEpoch;
    }

    internal static AgentRunLogIntegrity? ProjectIntegrity(int schemaVersion, byte[]? manifestDigest, long segmentCount, long totalBytes, DateTimeOffset? completedAt) => schemaVersion == 3
        ? new AgentRunLogIntegrity { Kind = AgentRunLogManifestDigest.Kind, ManifestDigest = manifestDigest == null ? null : Convert.ToHexStringLower(manifestDigest), VerifiedSegmentCount = manifestDigest == null ? null : segmentCount, VerifiedBytes = manifestDigest == null ? null : totalBytes, VerifiedAt = manifestDigest == null ? null : completedAt }
        : null;
    private static VerificationMutationResult VerificationRejected(AgentRunLogProblemCode code, bool retryable = false) => new(null, null, Problem(code, retryable));
    private sealed record VerificationMutation(AgentRunLogCompleteRequest Request, long? ExpectedCheckpointRevision = null, AgentRunLogManifestEntry? Entry = null, bool Seal = false);
    private sealed record VerificationMutationResult(AgentRunLogVerification? Verification, AgentRunLogMetadata? Metadata, AgentRunLogProblem? Problem, bool Sealed = false);
}
