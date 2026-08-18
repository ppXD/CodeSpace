using System.Buffers;
using System.Data;
using System.Data.Common;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Workflows.Artifacts.Runtime;
using CodeSpace.Messages.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace CodeSpace.Core.Services.Agents.AgentRunLogging;

/// <summary>
/// PostgreSQL metadata head over Artifact CAS v2 byte segments. Provider I/O never occurs inside a database
/// transaction; exact identities, worker/capture fences and monotonic heads make every retry replay-safe.
/// </summary>
public sealed partial class AgentRunLogService : IAgentRunLogService
{
    private const int MaximumAppendBytes = 4 * 1024 * 1024;
    private const int MaximumRangeBytes = 4 * 1024 * 1024;
    private const int CopyBufferBytes = 128 * 1024;
    private readonly DbContextOptions<CodeSpaceDbContext> _dbOptions;
    private readonly IArtifactCasRuntimeCoordinator _artifacts;
    private readonly TimeProvider _clock;

    public AgentRunLogService(DbContextOptions<CodeSpaceDbContext> dbOptions, IArtifactCasRuntimeCoordinator artifacts, TimeProvider clock)
    {
        _dbOptions = dbOptions;
        _artifacts = artifacts;
        _clock = clock;
    }

    public async Task<AgentRunLogOpenResult> OpenAsync(AgentRunLogOpenRequest request, CancellationToken cancellationToken)
    {
        if (!Valid(request, _clock.GetUtcNow())) return RejectOpen(AgentRunLogProblemCode.InvalidRequest);

        await using var db = CreateDb();
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var run = await db.AgentRun.FromSqlInterpolated($"SELECT agent_run.*, xmin FROM agent_run WHERE team_id = {request.TeamId} AND id = {request.AgentRunId} FOR UPDATE")
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (run == null) return RejectOpen(AgentRunLogProblemCode.Missing);
        if (run.Status != AgentRunStatus.Running) return RejectOpen(AgentRunLogProblemCode.RunNotRunning);
        if (run.FenceEpoch != request.WorkerFenceEpoch || request.WorkerFenceEpoch <= 0) return RejectOpen(AgentRunLogProblemCode.StaleWorker);

        var stream = await db.AgentRunLogStream.SingleOrDefaultAsync(value => value.TeamId == request.TeamId && value.AgentRunId == request.AgentRunId && value.StreamKind == request.StreamKind, cancellationToken).ConfigureAwait(false);
        if (stream == null)
        {
            var now = _clock.GetUtcNow();
            stream = new AgentRunLogStream
            {
                Id = Guid.NewGuid(), TeamId = request.TeamId, AgentRunId = request.AgentRunId,
                WorkerFenceEpoch = request.WorkerFenceEpoch, CaptureSessionId = request.CaptureSessionId,
                StreamKind = request.StreamKind, ContentType = request.ContentType, ContentEncoding = request.ContentEncoding,
                CaptureSource = request.CaptureSource, Retention = request.Retention, ExpiresAt = request.ExpiresAt,
                State = AgentRunLogStreamState.Open, Revision = 1, NextSegmentOrdinal = 1, SchemaVersion = 2,
                CreatedAt = now, LastModifiedAt = now,
            };
            db.AgentRunLogStream.Add(stream);
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return Opened(stream, false, false);
        }

        if (stream.SchemaVersion != 2) return RejectOpen(AgentRunLogProblemCode.Unsupported);
        if (stream.State != AgentRunLogStreamState.Open) return RejectOpen(AgentRunLogProblemCode.StreamTerminal);
        if (!SameIdentity(stream, request)) return RejectOpen(AgentRunLogProblemCode.CaptureClaimConflict);
        if (stream.WorkerFenceEpoch == request.WorkerFenceEpoch && stream.CaptureSessionId == request.CaptureSessionId)
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return Opened(stream, true, false);
        }
        if (stream.WorkerFenceEpoch > request.WorkerFenceEpoch) return RejectOpen(AgentRunLogProblemCode.StaleWorker);
        if (stream.CaptureSessionId != request.CaptureSessionId && stream.CaptureFinalizedAt == null)
            return RejectOpen(AgentRunLogProblemCode.CaptureClaimConflict);

        stream.WorkerFenceEpoch = request.WorkerFenceEpoch;
        if (stream.CaptureSessionId != request.CaptureSessionId)
        {
            stream.CaptureSessionId = request.CaptureSessionId;
            stream.CaptureSourceBaseOffsetBytes = stream.SourceOffsetBytes;
            stream.CaptureFinalizedAt = null;
        }
        stream.Revision++;
        stream.LastModifiedAt = _clock.GetUtcNow();
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return Opened(stream, false, true);
    }

    public async Task<AgentRunLogAppendResult> AppendAsync(AgentRunLogAppendRequest request, CancellationToken cancellationToken)
    {
        if (!Valid(request)) return RejectAppend(AgentRunLogProblemCode.InvalidRequest);
        var digest = SHA256.HashData(request.Bytes.Span);
        var before = await ReadAppendHeadAsync(request, digest, cancellationToken).ConfigureAwait(false);
        if (before.Result != null) return before.Result;

        await using var content = new MemoryStream(request.Bytes.ToArray(), writable: false);
        var transfer = await _artifacts.PutAsync(new ArtifactCasTransferRequest
        {
            TeamId = request.TeamId, StorageProfileId = request.StorageProfileId, StorageProfileRevision = request.StorageProfileRevision,
            IdempotencyKey = $"agent-run-log/{request.StreamId:N}/{request.ExpectedSegmentOrdinal}",
            TargetObjectKey = $"agent-runs/{request.AgentRunId:N}/logs/{request.StreamId:N}/{request.ExpectedSegmentOrdinal:D20}-{Convert.ToHexStringLower(digest)}",
            Content = content, ExpectedSizeBytes = request.Bytes.Length, ExpectedSha256 = Convert.ToHexStringLower(digest),
            ContentType = before.Stream!.ContentType, ActorId = request.ActorId, OperationTimeout = request.OperationTimeout,
        }, cancellationToken).ConfigureAwait(false);
        if (transfer is not ArtifactCasTransferResult.Committed committed)
            return new AgentRunLogAppendResult.Rejected(Map(transfer));

        var now = _clock.GetUtcNow();
        var segment = new AgentRunLogSegment
        {
            Id = Guid.NewGuid(), TeamId = request.TeamId, AgentRunId = request.AgentRunId, StreamId = request.StreamId,
            SegmentOrdinal = request.ExpectedSegmentOrdinal, StartOffsetBytes = request.ExpectedOffsetBytes,
            LengthBytes = request.Bytes.Length, ArtifactObjectId = committed.ArtifactObjectId,
            SourceStartOffsetBytes = request.ExpectedSourceOffsetBytes, SourceLengthBytes = request.SourceLengthBytes,
            WorkerFenceEpoch = request.WorkerFenceEpoch, CaptureSessionId = request.CaptureSessionId,
            FirstObservedAt = now, LastObservedAt = now, CreatedAt = now, SchemaVersion = 2,
        };

        await using var db = CreateDb();
        db.AgentRunLogSegment.Add(segment);
        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException)
        {
            var raced = await ReadAppendHeadAsync(request, digest, cancellationToken).ConfigureAwait(false);
            return raced.Result ?? RejectAppend(AgentRunLogProblemCode.ConcurrentMutation, true);
        }

        var metadata = await RequireMetadataAsync(request.TeamId, request.StreamId, cancellationToken).ConfigureAwait(false);
        return new AgentRunLogAppendResult.Appended(metadata, Receipt(segment), false);
    }

    public async Task<AgentRunLogCompleteResult> CompleteAsync(AgentRunLogCompleteRequest request, CancellationToken cancellationToken)
    {
        if (!Valid(request)) return RejectComplete(AgentRunLogProblemCode.InvalidRequest);
        var snapshot = await ReadSnapshotAsync(request.TeamId, request.StreamId, cancellationToken).ConfigureAwait(false);
        if (snapshot == null || snapshot.Metadata.AgentRunId != request.AgentRunId) return RejectComplete(AgentRunLogProblemCode.Missing);
        if (snapshot.Metadata.State != AgentRunLogStreamState.Open) return RejectComplete(AgentRunLogProblemCode.StreamTerminal);
        if (snapshot.WorkerFenceEpoch != request.WorkerFenceEpoch) return RejectComplete(AgentRunLogProblemCode.StaleWorker);
        if (snapshot.CaptureSessionId != request.CaptureSessionId) return RejectComplete(AgentRunLogProblemCode.CaptureClaimConflict);
        if (snapshot.Metadata.Revision != request.ExpectedRevision) return RejectComplete(AgentRunLogProblemCode.ConcurrentMutation, true);
        if (snapshot.CaptureFinalizedAt == null) return RejectComplete(AgentRunLogProblemCode.SourceNotFinalized);

        var hashed = await HashSegmentsAsync(snapshot, request.OperationTimeout, cancellationToken).ConfigureAwait(false);
        if (hashed.Problem != null) return new AgentRunLogCompleteResult.Rejected(hashed.Problem);

        await using var db = CreateDb();
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var run = await db.AgentRun.FromSqlInterpolated($"SELECT agent_run.*, xmin FROM agent_run WHERE team_id = {request.TeamId} AND id = {request.AgentRunId} FOR UPDATE")
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (run == null) return RejectComplete(AgentRunLogProblemCode.Missing);
        if (run.FenceEpoch != request.WorkerFenceEpoch) return RejectComplete(AgentRunLogProblemCode.StaleWorker);
        var stream = await db.AgentRunLogStream.SingleOrDefaultAsync(value => value.TeamId == request.TeamId && value.Id == request.StreamId, cancellationToken).ConfigureAwait(false);
        if (stream == null) return RejectComplete(AgentRunLogProblemCode.Missing);
        if (stream.State != AgentRunLogStreamState.Open) return RejectComplete(AgentRunLogProblemCode.StreamTerminal);
        if (stream.WorkerFenceEpoch != request.WorkerFenceEpoch || stream.CaptureSessionId != request.CaptureSessionId)
            return RejectComplete(AgentRunLogProblemCode.CaptureClaimConflict);
        if (stream.CaptureFinalizedAt == null) return RejectComplete(AgentRunLogProblemCode.SourceNotFinalized);
        if (stream.Revision != request.ExpectedRevision || stream.TotalBytes != hashed.TotalBytes)
            return RejectComplete(AgentRunLogProblemCode.ConcurrentMutation, true);
        if (request.RecoveryClaim != null)
        {
            if (!await CompleteUnderRecoveryClaimAsync(db, request, stream, hashed, cancellationToken).ConfigureAwait(false))
                return RejectComplete(AgentRunLogProblemCode.StaleRecoveryClaim);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new AgentRunLogCompleteResult.Completed(await RequireMetadataAsync(request.TeamId, request.StreamId, cancellationToken).ConfigureAwait(false));
        }

        stream.State = AgentRunLogStreamState.Completed;
        stream.ContentDigestAlgorithm = ArtifactDigestAlgorithm.Sha256;
        stream.ContentDigest = hashed.Digest;
        stream.Revision++;
        stream.CompletedAt = _clock.GetUtcNow();
        stream.LastModifiedAt = stream.CompletedAt.Value;
        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateConcurrencyException)
        {
            return RejectComplete(AgentRunLogProblemCode.ConcurrentMutation, true);
        }
        return new AgentRunLogCompleteResult.Completed(Project(stream));
    }

    public async Task<AgentRunLogFinalizeSourceResult> FinalizeSourceAsync(AgentRunLogFinalizeSourceRequest request, CancellationToken cancellationToken)
    {
        if (!Valid(request)) return RejectFinalizeSource(AgentRunLogProblemCode.InvalidRequest);
        await using var db = CreateDb();
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var run = await db.AgentRun.FromSqlInterpolated($"SELECT agent_run.*, xmin FROM agent_run WHERE team_id = {request.TeamId} AND id = {request.AgentRunId} FOR UPDATE")
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (run == null) return RejectFinalizeSource(AgentRunLogProblemCode.Missing);
        if (run.FenceEpoch != request.WorkerFenceEpoch) return RejectFinalizeSource(AgentRunLogProblemCode.StaleWorker);
        var stream = await db.AgentRunLogStream.SingleOrDefaultAsync(value => value.TeamId == request.TeamId && value.Id == request.StreamId && value.AgentRunId == request.AgentRunId, cancellationToken).ConfigureAwait(false);
        if (stream == null) return RejectFinalizeSource(AgentRunLogProblemCode.Missing);
        if (stream.State != AgentRunLogStreamState.Open) return RejectFinalizeSource(AgentRunLogProblemCode.StreamTerminal);
        if (stream.WorkerFenceEpoch != request.WorkerFenceEpoch || stream.CaptureSessionId != request.CaptureSessionId)
            return RejectFinalizeSource(AgentRunLogProblemCode.CaptureClaimConflict);
        if (stream.CaptureFinalizedAt != null)
            return stream.SourceOffsetBytes == request.ExpectedSourceOffsetBytes
                ? new AgentRunLogFinalizeSourceResult.Finalized(Project(stream), true)
                : RejectFinalizeSource(AgentRunLogProblemCode.NonContiguous);
        if (stream.Revision != request.ExpectedRevision || stream.SourceOffsetBytes != request.ExpectedSourceOffsetBytes)
            return RejectFinalizeSource(AgentRunLogProblemCode.ConcurrentMutation, true);

        stream.CaptureFinalizedAt = _clock.GetUtcNow();
        stream.Revision++;
        stream.LastModifiedAt = stream.CaptureFinalizedAt.Value;
        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateConcurrencyException)
        {
            return RejectFinalizeSource(AgentRunLogProblemCode.ConcurrentMutation, true);
        }
        return new AgentRunLogFinalizeSourceResult.Finalized(Project(stream), false);
    }

    public async Task<AgentRunLogFailCaptureResult> FailCaptureAsync(AgentRunLogFailCaptureRequest request, CancellationToken cancellationToken)
    {
        if (!Valid(request)) return RejectFailCapture(AgentRunLogProblemCode.InvalidRequest);
        await using var db = CreateDb();
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var run = await db.AgentRun.FromSqlInterpolated($"SELECT agent_run.*, xmin FROM agent_run WHERE team_id = {request.TeamId} AND id = {request.AgentRunId} FOR UPDATE")
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (run == null) return RejectFailCapture(AgentRunLogProblemCode.Missing);
        if (run.FenceEpoch != request.WorkerFenceEpoch) return RejectFailCapture(AgentRunLogProblemCode.StaleWorker);
        var stream = await db.AgentRunLogStream.SingleOrDefaultAsync(value => value.TeamId == request.TeamId && value.Id == request.StreamId && value.AgentRunId == request.AgentRunId, cancellationToken).ConfigureAwait(false);
        if (stream == null) return RejectFailCapture(AgentRunLogProblemCode.Missing);
        if (stream.State == request.TerminalState && stream.WorkerFenceEpoch == request.WorkerFenceEpoch && stream.CaptureSessionId == request.CaptureSessionId && stream.ErrorCode == request.ErrorCode && stream.ErrorMessage == request.ErrorMessage)
            return new AgentRunLogFailCaptureResult.Failed(Project(stream), true);
        if (stream.State != AgentRunLogStreamState.Open) return RejectFailCapture(AgentRunLogProblemCode.StreamTerminal);
        if (stream.WorkerFenceEpoch != request.WorkerFenceEpoch || stream.CaptureSessionId != request.CaptureSessionId)
            return RejectFailCapture(AgentRunLogProblemCode.CaptureClaimConflict);
        if (stream.Revision != request.ExpectedRevision) return RejectFailCapture(AgentRunLogProblemCode.ConcurrentMutation, true);
        if (request.RecoveryClaim != null)
        {
            if (!await FailUnderRecoveryClaimAsync(db, request, stream, cancellationToken).ConfigureAwait(false))
                return RejectFailCapture(AgentRunLogProblemCode.StaleRecoveryClaim);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new AgentRunLogFailCaptureResult.Failed(await RequireMetadataAsync(request.TeamId, request.StreamId, cancellationToken).ConfigureAwait(false), false);
        }

        stream.State = request.TerminalState;
        stream.ErrorCode = request.ErrorCode;
        stream.ErrorMessage = request.ErrorMessage;
        stream.Revision++;
        stream.CompletedAt = _clock.GetUtcNow();
        stream.LastModifiedAt = stream.CompletedAt.Value;
        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateConcurrencyException)
        {
            return RejectFailCapture(AgentRunLogProblemCode.ConcurrentMutation, true);
        }
        return new AgentRunLogFailCaptureResult.Failed(Project(stream), false);
    }

    public async Task<AgentRunLogMetadataResult> GetMetadataAsync(Guid teamId, Guid streamId, CancellationToken cancellationToken)
    {
        if (teamId == Guid.Empty || streamId == Guid.Empty) return new AgentRunLogMetadataResult.Missing();
        await using var db = CreateDb();
        var stream = await db.AgentRunLogStream.AsNoTracking().SingleOrDefaultAsync(value => value.TeamId == teamId && value.Id == streamId, cancellationToken).ConfigureAwait(false);
        return stream == null ? new AgentRunLogMetadataResult.Missing() : new AgentRunLogMetadataResult.Found(Project(stream));
    }

    public async Task<IReadOnlyList<AgentRunLogMetadata>> ListMetadataAsync(Guid teamId, Guid agentRunId, CancellationToken cancellationToken)
    {
        if (teamId == Guid.Empty || agentRunId == Guid.Empty) return [];
        await using var db = CreateDb();
        var streams = await db.AgentRunLogStream.AsNoTracking().Where(value => value.TeamId == teamId && value.AgentRunId == agentRunId)
            .OrderBy(value => value.StreamKind).ThenBy(value => value.Id).ToListAsync(cancellationToken).ConfigureAwait(false);
        return streams.Select(Project).ToArray();
    }

    public async Task<IReadOnlyList<AgentRunLogCaptureHead>> ListCaptureHeadsAsync(Guid teamId, Guid agentRunId, CancellationToken cancellationToken)
    {
        if (teamId == Guid.Empty || agentRunId == Guid.Empty) return [];
        await using var db = CreateDb();
        var streams = await db.AgentRunLogStream.AsNoTracking().Where(value => value.TeamId == teamId && value.AgentRunId == agentRunId)
            .OrderBy(value => value.StreamKind).ThenBy(value => value.Id).ToListAsync(cancellationToken).ConfigureAwait(false);
        return streams.Where(value => value.WorkerFenceEpoch != null && value.CaptureSessionId != null).Select(CaptureHead).ToArray();
    }

    public async Task<AgentRunLogRangeResult> ReadRangeAsync(AgentRunLogRangeRequest request, CancellationToken cancellationToken)
    {
        if (request.TeamId == Guid.Empty || request.StreamId == Guid.Empty || request.OffsetBytes < 0 || request.Length <= 0 || request.Length > MaximumRangeBytes || request.OffsetBytes > long.MaxValue - request.Length || !ValidTimeout(request.OperationTimeout))
            return RejectRange(AgentRunLogProblemCode.InvalidRequest);
        var requestedEndUnbounded = request.OffsetBytes + request.Length;
        var snapshot = await ReadSnapshotAsync(request.TeamId, request.StreamId, cancellationToken, request.OffsetBytes, requestedEndUnbounded).ConfigureAwait(false);
        if (snapshot == null) return RejectRange(AgentRunLogProblemCode.Missing);
        if (request.OffsetBytes > snapshot.Metadata.TotalBytes) return RejectRange(AgentRunLogProblemCode.InvalidRequest, metadata: snapshot.Metadata);

        var requestedEnd = Math.Min(snapshot.Metadata.TotalBytes, checked(request.OffsetBytes + request.Length));
        if (requestedEnd == request.OffsetBytes) return new AgentRunLogRangeResult.Available(snapshot.Metadata, request.OffsetBytes, []);
        using var output = new MemoryStream((int)(requestedEnd - request.OffsetBytes));
        foreach (var segment in snapshot.Segments.Where(value => value.StartOffset < requestedEnd && value.StartOffset + value.Length > request.OffsetBytes))
        {
            var opened = await OpenRequiredAsync(request.TeamId, segment, request.OperationTimeout, cancellationToken).ConfigureAwait(false);
            if (opened.Result == null) return new AgentRunLogRangeResult.Unavailable(opened.Problem!, snapshot.Metadata);
            await using var content = opened.Result.Content;
            var copyProblem = await CopyRangeAndVerifyAsync(content, segment, new RangeCopy(request.OffsetBytes, requestedEnd, output), cancellationToken).ConfigureAwait(false);
            if (copyProblem != null) return new AgentRunLogRangeResult.Unavailable(copyProblem, snapshot.Metadata);
        }
        if (output.Length != requestedEnd - request.OffsetBytes) return RejectRange(AgentRunLogProblemCode.ArtifactMissing, metadata: snapshot.Metadata);
        return new AgentRunLogRangeResult.Available(snapshot.Metadata, request.OffsetBytes, output.ToArray());
    }

    private async Task<AppendHead> ReadAppendHeadAsync(AgentRunLogAppendRequest request, byte[] digest, CancellationToken cancellationToken)
    {
        await using var db = CreateDb();
        var currentFence = await db.AgentRun.AsNoTracking().Where(value => value.TeamId == request.TeamId && value.Id == request.AgentRunId).Select(value => (long?)value.FenceEpoch).SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (currentFence == null) return new AppendHead(null, RejectAppend(AgentRunLogProblemCode.Missing));
        if (currentFence != request.WorkerFenceEpoch) return new AppendHead(null, RejectAppend(AgentRunLogProblemCode.StaleWorker));
        var stream = await db.AgentRunLogStream.AsNoTracking().SingleOrDefaultAsync(value => value.TeamId == request.TeamId && value.Id == request.StreamId && value.AgentRunId == request.AgentRunId, cancellationToken).ConfigureAwait(false);
        if (stream == null) return new AppendHead(null, RejectAppend(AgentRunLogProblemCode.Missing));
        if (stream.SchemaVersion != 2) return new AppendHead(stream, RejectAppend(AgentRunLogProblemCode.Unsupported));
        if (stream.State != AgentRunLogStreamState.Open) return new AppendHead(stream, RejectAppend(AgentRunLogProblemCode.StreamTerminal));
        if (stream.CaptureFinalizedAt != null) return new AppendHead(stream, RejectAppend(AgentRunLogProblemCode.CaptureClaimConflict));
        if (stream.WorkerFenceEpoch != request.WorkerFenceEpoch) return new AppendHead(stream, RejectAppend(AgentRunLogProblemCode.StaleWorker));
        if (stream.CaptureSessionId != request.CaptureSessionId) return new AppendHead(stream, RejectAppend(AgentRunLogProblemCode.CaptureClaimConflict));
        var existing = await (from segment in db.AgentRunLogSegment.AsNoTracking()
                              join artifact in db.ArtifactObject.AsNoTracking() on new { segment.TeamId, Id = segment.ArtifactObjectId } equals new { artifact.TeamId, artifact.Id }
                              where segment.TeamId == request.TeamId && segment.StreamId == request.StreamId && segment.SegmentOrdinal == request.ExpectedSegmentOrdinal
                              select new { Segment = segment, artifact.Digest }).SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (existing != null)
        {
            var exact = existing.Segment.StartOffsetBytes == request.ExpectedOffsetBytes && existing.Segment.LengthBytes == request.Bytes.Length
                && existing.Segment.SourceStartOffsetBytes == request.ExpectedSourceOffsetBytes && existing.Segment.SourceLengthBytes == request.SourceLengthBytes
                && existing.Segment.WorkerFenceEpoch == request.WorkerFenceEpoch && existing.Segment.CaptureSessionId == request.CaptureSessionId
                && CryptographicOperations.FixedTimeEquals(existing.Digest, digest);
            return exact
                ? new AppendHead(stream, new AgentRunLogAppendResult.Appended(Project(stream), Receipt(existing.Segment), true))
                : new AppendHead(stream, RejectAppend(AgentRunLogProblemCode.IdempotencyConflict));
        }
        if (stream.NextSegmentOrdinal != request.ExpectedSegmentOrdinal || stream.NextOffsetBytes != request.ExpectedOffsetBytes || stream.SourceOffsetBytes != request.ExpectedSourceOffsetBytes)
            return new AppendHead(stream, RejectAppend(AgentRunLogProblemCode.NonContiguous));
        return new AppendHead(stream, null);
    }

    private async Task<LogSnapshot?> ReadSnapshotAsync(Guid teamId, Guid streamId, CancellationToken cancellationToken, long? rangeStart = null, long? rangeEnd = null)
    {
        await using var db = CreateDb();
        var stream = await db.AgentRunLogStream.AsNoTracking().SingleOrDefaultAsync(value => value.TeamId == teamId && value.Id == streamId, cancellationToken).ConfigureAwait(false);
        if (stream == null) return null;
        var segments = await (from segment in db.AgentRunLogSegment.AsNoTracking()
                              join artifact in db.ArtifactObject.AsNoTracking()
                                  on new { segment.TeamId, Id = segment.ArtifactObjectId } equals new { artifact.TeamId, artifact.Id }
                              join location in db.ArtifactLocation.AsNoTracking().Where(value => value.State == ArtifactLocationState.Available)
                                  on new { segment.TeamId, segment.ArtifactObjectId } equals new { location.TeamId, location.ArtifactObjectId }
                              join revision in db.StorageProfileRevision.AsNoTracking()
                                  on new { location.TeamId, Id = location.StorageProfileRevisionId } equals new { revision.TeamId, revision.Id }
                              where segment.TeamId == teamId && segment.StreamId == streamId
                                  && (rangeStart == null || (segment.StartOffsetBytes < rangeEnd!.Value && segment.StartOffsetBytes + segment.LengthBytes > rangeStart.Value))
                              orderby segment.SegmentOrdinal, location.VerifiedAt descending, location.Id
                              select new SegmentLocationRow(segment.SegmentOrdinal, segment.StartOffsetBytes, segment.LengthBytes, segment.ArtifactObjectId, artifact.Digest, revision.StorageProfileId, revision.Revision))
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var selected = segments.GroupBy(value => value.Ordinal).Select(group =>
        {
            var first = group.First();
            return new SegmentSource(first.Ordinal, first.StartOffset, first.Length, first.ArtifactObjectId, first.Digest,
                group.Select(value => new SegmentLocation(value.StorageProfileId, value.StorageProfileRevision)).Distinct().ToArray());
        }).OrderBy(value => value.Ordinal).ToArray();
        return new LogSnapshot(Project(stream), stream.WorkerFenceEpoch, stream.CaptureSessionId, stream.CaptureFinalizedAt, selected) { TeamId = teamId };
    }

    private async Task<HashResult> HashSegmentsAsync(LogSnapshot snapshot, TimeSpan? timeout, CancellationToken cancellationToken)
    {
        if (snapshot.Segments.Length != snapshot.Metadata.SegmentCount) return new HashResult(null, 0, Problem(AgentRunLogProblemCode.ArtifactMissing));
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        long total = 0;
        var buffer = ArrayPool<byte>.Shared.Rent(CopyBufferBytes);
        try
        {
            long expectedOrdinal = 1;
            foreach (var segment in snapshot.Segments)
            {
                if (segment.Ordinal != expectedOrdinal || segment.StartOffset != total)
                    return new HashResult(null, total, Problem(AgentRunLogProblemCode.ArtifactCorrupt));
                var opened = await OpenRequiredAsync(snapshot.TeamId, segment, timeout, cancellationToken).ConfigureAwait(false);
                if (opened.Result == null) return new HashResult(null, total, opened.Problem);
                await using var content = opened.Result.Content;
                long observed = 0;
                try
                {
                    while (true)
                    {
                        var read = await content.ReadAsync(buffer.AsMemory(0, CopyBufferBytes), cancellationToken).ConfigureAwait(false);
                        if (read == 0) break;
                        hash.AppendData(buffer, 0, read);
                        observed += read;
                    }
                }
                catch (InvalidDataException) { return new HashResult(null, total, Problem(AgentRunLogProblemCode.ArtifactCorrupt)); }
                catch (UnauthorizedAccessException) { return new HashResult(null, total, Problem(AgentRunLogProblemCode.AccessDenied)); }
                catch (IOException) { return new HashResult(null, total, Problem(AgentRunLogProblemCode.BackendUnavailable, true)); }
                if (observed != segment.Length) return new HashResult(null, total, Problem(AgentRunLogProblemCode.ArtifactCorrupt));
                total += observed;
                expectedOrdinal++;
            }
            return total == snapshot.Metadata.TotalBytes
                ? new HashResult(hash.GetHashAndReset(), total, null)
                : new HashResult(null, total, Problem(AgentRunLogProblemCode.ArtifactCorrupt));
        }
        finally { ArrayPool<byte>.Shared.Return(buffer); }
    }

    private async Task<OpenedArtifact> OpenRequiredAsync(Guid teamId, SegmentSource segment, TimeSpan? timeout, CancellationToken cancellationToken)
    {
        var problems = new List<AgentRunLogProblem>(segment.Locations.Length);
        foreach (var location in segment.Locations)
        {
            var result = await _artifacts.OpenReadAsync(new ArtifactCasReadRequest
            {
                TeamId = teamId, ArtifactObjectId = segment.ArtifactObjectId,
                StorageProfileId = location.StorageProfileId, StorageProfileRevision = location.StorageProfileRevision,
                OperationTimeout = timeout,
            }, cancellationToken).ConfigureAwait(false);
            if (result is ArtifactCasReadResult.Opened opened)
            {
                if (opened.SizeBytes == segment.Length && string.Equals(opened.Sha256, Convert.ToHexStringLower(segment.Digest), StringComparison.OrdinalIgnoreCase))
                    return new OpenedArtifact(opened, null);
                await opened.Content.DisposeAsync().ConfigureAwait(false);
                problems.Add(Problem(AgentRunLogProblemCode.ArtifactCorrupt));
            }
            else if (result is ArtifactCasReadResult.Unavailable unavailable)
            {
                problems.Add(Map(unavailable.Problem));
            }
            else
            {
                problems.Add(Problem(AgentRunLogProblemCode.BackendUnavailable, true));
            }
        }
        return new OpenedArtifact(null, SelectReadProblem(problems));
    }

    private static AgentRunLogProblem SelectReadProblem(IReadOnlyCollection<AgentRunLogProblem> problems) =>
        problems.FirstOrDefault(value => value.IsRetryable)
        ?? problems.FirstOrDefault(value => value.Code == AgentRunLogProblemCode.ArtifactCorrupt)
        ?? problems.FirstOrDefault(value => value.Code == AgentRunLogProblemCode.AccessDenied)
        ?? problems.FirstOrDefault()
        ?? Problem(AgentRunLogProblemCode.ArtifactMissing);

    private static async Task<AgentRunLogProblem?> CopyRangeAndVerifyAsync(Stream content, SegmentSource segment, RangeCopy range, CancellationToken cancellationToken)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(CopyBufferBytes);
        long observed = 0;
        try
        {
            while (true)
            {
                int read;
                try { read = await content.ReadAsync(buffer.AsMemory(0, CopyBufferBytes), cancellationToken).ConfigureAwait(false); }
                catch (InvalidDataException) { return Problem(AgentRunLogProblemCode.ArtifactCorrupt); }
                catch (UnauthorizedAccessException) { return Problem(AgentRunLogProblemCode.AccessDenied); }
                catch (IOException) { return Problem(AgentRunLogProblemCode.BackendUnavailable, true); }
                if (read == 0) break;
                var absoluteStart = segment.StartOffset + observed;
                var copyStart = Math.Max(absoluteStart, range.RequestedStart);
                var copyEnd = Math.Min(absoluteStart + read, range.RequestedEnd);
                if (copyEnd > copyStart)
                    await range.Output.WriteAsync(buffer.AsMemory((int)(copyStart - absoluteStart), (int)(copyEnd - copyStart)), cancellationToken).ConfigureAwait(false);
                observed += read;
            }
            return observed == segment.Length ? null : Problem(AgentRunLogProblemCode.ArtifactCorrupt);
        }
        finally { ArrayPool<byte>.Shared.Return(buffer); }
    }

    private async Task<AgentRunLogMetadata> RequireMetadataAsync(Guid teamId, Guid streamId, CancellationToken cancellationToken) =>
        (await GetMetadataAsync(teamId, streamId, cancellationToken).ConfigureAwait(false) as AgentRunLogMetadataResult.Found)?.Metadata
        ?? throw new InvalidOperationException("The committed Agent Run log stream metadata disappeared.");

    private static Task<bool> CompleteUnderRecoveryClaimAsync(CodeSpaceDbContext db, AgentRunLogCompleteRequest request, AgentRunLogStream stream, HashResult hash, CancellationToken cancellationToken)
    {
        var claim = request.RecoveryClaim!;
        return ExecuteRecoveryCommitAsync(db, """
            WITH authority AS MATERIALIZED (
                SELECT intent.id FROM agent_run_log_capture_intent intent
                WHERE intent.id = @intent_id AND intent.team_id = @team_id AND intent.agent_run_id = @agent_run_id
                  AND intent.worker_fence_epoch = @worker_fence_epoch AND intent.capture_session_id = @capture_session_id
                  AND intent.stream_kind = @stream_kind AND intent.content_type = @content_type
                  AND intent.content_encoding IS NOT DISTINCT FROM @content_encoding AND intent.capture_source = @capture_source
                  AND (intent.stream_id IS NULL OR intent.stream_id = @stream_id)
                  AND intent.state IN ('Expected', 'Opened', 'SourceFinalized')
                  AND intent.recovery_owner_id = @recovery_owner_id AND intent.recovery_fence_epoch = @recovery_fence_epoch
                  AND intent.recovery_lease_expires_at > clock_timestamp()
                FOR UPDATE
            ), committed_at AS MATERIALIZED (
                SELECT clock_timestamp() AS value
            ), updated AS (
                UPDATE agent_run_log_stream target SET
                    state = 'Completed', content_digest_algorithm = 'Sha256', content_digest = @content_digest,
                    revision = target.revision + 1, completed_at = committed_at.value, last_modified_at = committed_at.value
                FROM authority, committed_at
                WHERE target.team_id = @team_id AND target.agent_run_id = @agent_run_id AND target.id = @stream_id
                  AND target.worker_fence_epoch = @worker_fence_epoch AND target.capture_session_id = @capture_session_id
                  AND target.state = 'Open' AND target.capture_finalized_at IS NOT NULL
                  AND target.revision = @expected_revision AND target.total_bytes = @total_bytes
                RETURNING target.id
            )
            SELECT EXISTS (SELECT 1 FROM updated)
            """, RecoveryCommitParameters(request, stream,
            new RecoveryCommitPayload(claim, hash.Digest!, hash.TotalBytes, null, null)), cancellationToken);
    }

    private static Task<bool> FailUnderRecoveryClaimAsync(CodeSpaceDbContext db, AgentRunLogFailCaptureRequest request, AgentRunLogStream stream, CancellationToken cancellationToken)
    {
        var claim = request.RecoveryClaim!;
        return ExecuteRecoveryCommitAsync(db, """
            WITH authority AS MATERIALIZED (
                SELECT intent.id FROM agent_run_log_capture_intent intent
                WHERE intent.id = @intent_id AND intent.team_id = @team_id AND intent.agent_run_id = @agent_run_id
                  AND intent.worker_fence_epoch = @worker_fence_epoch AND intent.capture_session_id = @capture_session_id
                  AND intent.stream_kind = @stream_kind AND intent.content_type = @content_type
                  AND intent.content_encoding IS NOT DISTINCT FROM @content_encoding AND intent.capture_source = @capture_source
                  AND (intent.stream_id IS NULL OR intent.stream_id = @stream_id)
                  AND intent.state IN ('Expected', 'Opened', 'SourceFinalized')
                  AND intent.recovery_owner_id = @recovery_owner_id AND intent.recovery_fence_epoch = @recovery_fence_epoch
                  AND intent.recovery_lease_expires_at > clock_timestamp()
                FOR UPDATE
            ), committed_at AS MATERIALIZED (
                SELECT clock_timestamp() AS value
            ), updated AS (
                UPDATE agent_run_log_stream target SET
                    state = 'CaptureFailed', error_code = @error_code, error_message = @error_message,
                    revision = target.revision + 1, completed_at = committed_at.value, last_modified_at = committed_at.value
                FROM authority, committed_at
                WHERE target.team_id = @team_id AND target.agent_run_id = @agent_run_id AND target.id = @stream_id
                  AND target.worker_fence_epoch = @worker_fence_epoch AND target.capture_session_id = @capture_session_id
                  AND target.state = 'Open' AND target.revision = @expected_revision
                RETURNING target.id
            )
            SELECT EXISTS (SELECT 1 FROM updated)
            """, RecoveryCommitParameters(request, stream,
            new RecoveryCommitPayload(claim, null, null, request.ErrorCode, request.ErrorMessage)), cancellationToken);
    }

    private static async Task<bool> ExecuteRecoveryCommitAsync(CodeSpaceDbContext db, string sql, IReadOnlyList<RecoveryCommitParameter> parameters, CancellationToken cancellationToken)
    {
        await using var command = db.Database.GetDbConnection().CreateCommand();
        command.Transaction = db.Database.CurrentTransaction?.GetDbTransaction()
            ?? throw new InvalidOperationException("A recovery-fenced stream commit requires an active database transaction.");
        command.CommandText = sql;
        foreach (var value in parameters) command.Parameters.Add(Parameter(command, value));
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is true;
    }

    private static DbParameter Parameter(DbCommand command, RecoveryCommitParameter value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = value.Name;
        parameter.DbType = value.Type;
        parameter.Value = value.Value ?? DBNull.Value;
        return parameter;
    }

    private static RecoveryCommitParameter[] RecoveryCommitParameters(AgentRunLogCompleteRequest request, AgentRunLogStream stream, RecoveryCommitPayload payload) =>
        CommonRecoveryCommitParameters(new RecoveryCommitIdentity(request.TeamId, request.AgentRunId, request.StreamId, request.WorkerFenceEpoch, request.CaptureSessionId, request.ExpectedRevision), stream, payload)
            .Concat([
                new("content_digest", DbType.Binary, payload.ContentDigest),
                new("total_bytes", DbType.Int64, payload.TotalBytes),
            ]).ToArray();

    private static RecoveryCommitParameter[] RecoveryCommitParameters(AgentRunLogFailCaptureRequest request, AgentRunLogStream stream, RecoveryCommitPayload payload) =>
        CommonRecoveryCommitParameters(new RecoveryCommitIdentity(request.TeamId, request.AgentRunId, request.StreamId, request.WorkerFenceEpoch, request.CaptureSessionId, request.ExpectedRevision), stream, payload)
            .Concat([
                new("error_code", DbType.String, payload.ErrorCode),
                new("error_message", DbType.String, payload.ErrorMessage),
            ]).ToArray();

    private static RecoveryCommitParameter[] CommonRecoveryCommitParameters(RecoveryCommitIdentity identity, AgentRunLogStream stream, RecoveryCommitPayload payload) =>
    [
        new("intent_id", DbType.Guid, payload.Claim.IntentId), new("team_id", DbType.Guid, identity.TeamId), new("agent_run_id", DbType.Guid, identity.AgentRunId),
        new("worker_fence_epoch", DbType.Int64, identity.WorkerFenceEpoch), new("capture_session_id", DbType.Guid, identity.CaptureSessionId),
        new("stream_kind", DbType.String, stream.StreamKind), new("content_type", DbType.String, stream.ContentType),
        new("content_encoding", DbType.String, stream.ContentEncoding), new("capture_source", DbType.String, stream.CaptureSource),
        new("stream_id", DbType.Guid, identity.StreamId), new("recovery_owner_id", DbType.Guid, payload.Claim.OwnerId),
        new("recovery_fence_epoch", DbType.Int64, payload.Claim.FenceEpoch), new("expected_revision", DbType.Int64, identity.ExpectedRevision),
    ];

    private CodeSpaceDbContext CreateDb() => new(_dbOptions);
    private static AgentRunLogOpenResult.Opened Opened(AgentRunLogStream value, bool alreadyOpen, bool reclaimed) => new(Project(value), alreadyOpen, reclaimed) { CaptureSourceBaseOffsetBytes = value.CaptureSourceBaseOffsetBytes, CaptureFinalizedAt = value.CaptureFinalizedAt };
    private static AgentRunLogCaptureHead CaptureHead(AgentRunLogStream value) => new(Project(value), value.WorkerFenceEpoch!.Value, value.CaptureSessionId!.Value, value.CaptureSourceBaseOffsetBytes, value.CaptureFinalizedAt);
    private static AgentRunLogSegmentReceipt Receipt(AgentRunLogSegment value) => new(value.Id, value.SegmentOrdinal, value.StartOffsetBytes, value.LengthBytes, value.SourceStartOffsetBytes, value.SourceLengthBytes, value.ArtifactObjectId);
    private static AgentRunLogMetadata Project(AgentRunLogStream value) => new(value.Id, value.AgentRunId, value.StreamKind, value.ContentType, value.ContentEncoding, value.CaptureSource, value.Retention, value.State, value.Revision, value.SegmentCount, value.TotalBytes, value.SourceOffsetBytes, value.ContentDigest == null ? null : Convert.ToHexStringLower(value.ContentDigest), value.CreatedAt, value.LastModifiedAt, value.CompletedAt, value.ErrorCode);
    private static bool SameIdentity(AgentRunLogStream stream, AgentRunLogOpenRequest request) => stream.ContentType == request.ContentType && stream.ContentEncoding == request.ContentEncoding && stream.CaptureSource == request.CaptureSource && stream.Retention == request.Retention && stream.ExpiresAt == request.ExpiresAt;
    private static bool Valid(AgentRunLogOpenRequest value, DateTimeOffset now) => value.TeamId != Guid.Empty && value.AgentRunId != Guid.Empty && value.WorkerFenceEpoch > 0 && value.CaptureSessionId != Guid.Empty && KeyPattern().IsMatch(value.StreamKind ?? "") && KeyPattern().IsMatch(value.CaptureSource ?? "") && value.ContentType is { Length: <= 255 } && ContentTypePattern().IsMatch(value.ContentType) && (value.ContentEncoding == null || EncodingPattern().IsMatch(value.ContentEncoding)) && Enum.IsDefined(value.Retention) && (value.ExpiresAt == null || value.ExpiresAt > now) && (value.Retention != ArtifactRetention.Ephemeral || value.ExpiresAt != null) && (value.Retention != ArtifactRetention.Permanent || value.ExpiresAt == null);
    private static bool Valid(AgentRunLogAppendRequest value) => value.TeamId != Guid.Empty && value.AgentRunId != Guid.Empty && value.StreamId != Guid.Empty && value.WorkerFenceEpoch > 0 && value.CaptureSessionId != Guid.Empty && value.ExpectedSegmentOrdinal > 0 && value.ExpectedOffsetBytes >= 0 && value.ExpectedSourceOffsetBytes >= 0 && value.SourceLengthBytes > 0 && value.StorageProfileId != Guid.Empty && value.StorageProfileRevision > 0 && value.ActorId != Guid.Empty && value.Bytes.Length is > 0 and <= MaximumAppendBytes && ValidTimeout(value.OperationTimeout);
    private static bool Valid(AgentRunLogFinalizeSourceRequest value) => value.TeamId != Guid.Empty && value.AgentRunId != Guid.Empty && value.StreamId != Guid.Empty && value.WorkerFenceEpoch > 0 && value.CaptureSessionId != Guid.Empty && value.ExpectedRevision > 0 && value.ExpectedSourceOffsetBytes >= 0;
    private static bool Valid(AgentRunLogCompleteRequest value) => value.TeamId != Guid.Empty && value.AgentRunId != Guid.Empty && value.StreamId != Guid.Empty && value.WorkerFenceEpoch > 0 && value.CaptureSessionId != Guid.Empty && value.ExpectedRevision > 0 && Valid(value.RecoveryClaim) && ValidTimeout(value.OperationTimeout);
    private static bool Valid(AgentRunLogFailCaptureRequest value) => value.TeamId != Guid.Empty && value.AgentRunId != Guid.Empty && value.StreamId != Guid.Empty && value.WorkerFenceEpoch > 0 && value.CaptureSessionId != Guid.Empty && value.ExpectedRevision > 0 && Valid(value.RecoveryClaim) && ValidTerminal(value.TerminalState, value.RecoveryClaim) && ErrorCodePattern().IsMatch(value.ErrorCode ?? "") && (value.ErrorMessage == null || value.ErrorMessage.Length <= 2048);
    private static bool Valid(AgentRunLogRecoveryClaimRef? value) => value == null || (value.IntentId != Guid.Empty && value.OwnerId != Guid.Empty && value.FenceEpoch > 0);
    /// <summary>The terminal states this seam may write: capture broke, or capture succeeded on a source its own size cap cut short. A recovery-claimed commit is CaptureFailed only — its fenced statement writes that state literally.</summary>
    private static bool ValidTerminal(AgentRunLogStreamState value, AgentRunLogRecoveryClaimRef? claim) => value == AgentRunLogStreamState.CaptureFailed || (value == AgentRunLogStreamState.Truncated && claim == null);
    private static bool ValidTimeout(TimeSpan? value) => value == null || (value > TimeSpan.Zero && value <= TimeSpan.FromMinutes(10));
    private static AgentRunLogOpenResult.Rejected RejectOpen(AgentRunLogProblemCode code, bool retryable = false) => new(Problem(code, retryable));
    private static AgentRunLogAppendResult.Rejected RejectAppend(AgentRunLogProblemCode code, bool retryable = false) => new(Problem(code, retryable));
    private static AgentRunLogFinalizeSourceResult.Rejected RejectFinalizeSource(AgentRunLogProblemCode code, bool retryable = false) => new(Problem(code, retryable));
    private static AgentRunLogCompleteResult.Rejected RejectComplete(AgentRunLogProblemCode code, bool retryable = false) => new(Problem(code, retryable));
    private static AgentRunLogFailCaptureResult.Rejected RejectFailCapture(AgentRunLogProblemCode code, bool retryable = false) => new(Problem(code, retryable));
    private static AgentRunLogRangeResult.Unavailable RejectRange(AgentRunLogProblemCode code, bool retryable = false, AgentRunLogMetadata? metadata = null) => new(Problem(code, retryable), metadata);
    private static AgentRunLogProblem Problem(AgentRunLogProblemCode code, bool retryable = false) => new(code, retryable);
    private static AgentRunLogProblem Map(ArtifactCasTransferResult result) => result switch
    {
        ArtifactCasTransferResult.Deferred deferred => Map(deferred.Problem),
        ArtifactCasTransferResult.Rejected rejected => Map(rejected.Problem),
        _ => Problem(AgentRunLogProblemCode.BackendUnavailable, true),
    };
    private static AgentRunLogProblem Map(ArtifactCasProblem value) => value.Code switch
    {
        ArtifactCasProblemCode.IdempotencyConflict => Problem(AgentRunLogProblemCode.IdempotencyConflict),
        ArtifactCasProblemCode.ArtifactMissing or ArtifactCasProblemCode.TargetMissing => Problem(AgentRunLogProblemCode.ArtifactMissing),
        ArtifactCasProblemCode.TargetCorrupt => Problem(AgentRunLogProblemCode.ArtifactCorrupt),
        ArtifactCasProblemCode.Unauthorized or ArtifactCasProblemCode.Forbidden => Problem(AgentRunLogProblemCode.AccessDenied),
        ArtifactCasProblemCode.ProviderTimeout => Problem(AgentRunLogProblemCode.ProviderTimeout, value.IsRetryable),
        ArtifactCasProblemCode.Unsupported => Problem(AgentRunLogProblemCode.Unsupported),
        ArtifactCasProblemCode.StaleWorker => Problem(AgentRunLogProblemCode.StaleWorker),
        _ => Problem(AgentRunLogProblemCode.BackendUnavailable, value.IsRetryable),
    };

    [GeneratedRegex("^[a-z0-9][a-z0-9._/-]{0,126}/v[1-9][0-9]*$", RegexOptions.CultureInvariant)]
    private static partial Regex KeyPattern();
    [GeneratedRegex("^[a-z0-9][a-z0-9._+-]{0,63}$", RegexOptions.CultureInvariant)]
    private static partial Regex EncodingPattern();
    [GeneratedRegex("^[^\\s/]+/[^\\s/]+$", RegexOptions.CultureInvariant)]
    private static partial Regex ContentTypePattern();
    [GeneratedRegex("^[a-z0-9][a-z0-9.-]{0,127}$", RegexOptions.CultureInvariant)]
    private static partial Regex ErrorCodePattern();

    private sealed record AppendHead(AgentRunLogStream? Stream, AgentRunLogAppendResult? Result);
    private sealed record SegmentLocationRow(long Ordinal, long StartOffset, long Length, Guid ArtifactObjectId, byte[] Digest, Guid StorageProfileId, int StorageProfileRevision);
    private sealed record SegmentLocation(Guid StorageProfileId, int StorageProfileRevision);
    private sealed record SegmentSource(long Ordinal, long StartOffset, long Length, Guid ArtifactObjectId, byte[] Digest, SegmentLocation[] Locations);
    private sealed record RangeCopy(long RequestedStart, long RequestedEnd, Stream Output);
    private sealed record RecoveryCommitIdentity(Guid TeamId, Guid AgentRunId, Guid StreamId, long WorkerFenceEpoch, Guid CaptureSessionId, long ExpectedRevision);
    private sealed record RecoveryCommitPayload(AgentRunLogRecoveryClaimRef Claim, byte[]? ContentDigest, long? TotalBytes, string? ErrorCode, string? ErrorMessage);
    private sealed record RecoveryCommitParameter(string Name, DbType Type, object? Value);
    private sealed record LogSnapshot(AgentRunLogMetadata Metadata, long? WorkerFenceEpoch, Guid? CaptureSessionId, DateTimeOffset? CaptureFinalizedAt, SegmentSource[] Segments)
    {
        public Guid TeamId { get; init; }
    }
    private sealed record HashResult(byte[]? Digest, long TotalBytes, AgentRunLogProblem? Problem);
    private sealed record OpenedArtifact(ArtifactCasReadResult.Opened? Result, AgentRunLogProblem? Problem);
}
