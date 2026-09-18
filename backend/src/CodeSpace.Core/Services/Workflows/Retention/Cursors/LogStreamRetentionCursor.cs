using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Workflows.Artifacts.Runtime;
using CodeSpace.Messages.Artifacts;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Retention;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CodeSpace.Core.Services.Workflows.Retention.Cursors;

/// <summary>
/// Reclaims the routed CAS bytes behind a terminal agent-run log stream that nothing cites any more.
///
/// <para><b>The bytes go, the head row stays.</b> A purge removes the segment objects' bytes and then stamps
/// <c>purged_at</c> on the stream head; the head, its segments and its verification manifest survive. That is
/// deliberate: a reader that finds no stream at all cannot tell "reclaimed by policy" from "capture lost it", and the
/// entire point of a retention plane is that reclamation is legible. The Room reads the tombstone and says purged.</para>
///
/// <para><b>Order, and what a crash leaves.</b> The bytes go first and the tombstone commits last. A crash in between
/// leaves bytes gone and no tombstone — the next sweep re-asks, finds the locations already Purged, and finishes the
/// stamp; the drain is idempotent by construction. The opposite order would leave bytes that no surviving row
/// remembers how to reach, which is the one outcome nothing can repair.</para>
///
/// <para><b>What is never collected.</b> A stream a sealed qualification result pins. A stream whose object another
/// stream's segment — or a <c>workflow_artifact</c> row — also names, because the CAS is content-addressed and those
/// are ONE physical object. And anything at all when the question could not be asked: every failure answers
/// <see cref="DurableReferenceVerdict.Indeterminate"/>, never "unreferenced".</para>
/// </summary>
public sealed class LogStreamRetentionCursor : IDurableRetentionCursor, IScopedDependency
{
    /// <summary>
    /// Objects purged per stream per sweep. A 4 GiB capture is thousands of segments, and a sweep that tried to drain
    /// one in a single pass would hold a worker for minutes on provider I/O. Partial drains cost nothing: the tombstone
    /// is only stamped once every object is gone, and the next sweep continues from whatever is left.
    /// </summary>
    private const int MaxObjectsPerSweep = 64;

    private readonly DbContextOptions<CodeSpaceDbContext> _dbOptions;
    private readonly IArtifactCasPurgeCoordinator _purge;
    private readonly ILogger<LogStreamRetentionCursor> _logger;

    public LogStreamRetentionCursor(DbContextOptions<CodeSpaceDbContext> dbOptions, IArtifactCasPurgeCoordinator purge, ILogger<LogStreamRetentionCursor> logger)
    {
        _dbOptions = dbOptions;
        _purge = purge;
        _logger = logger;
    }

    public DurableRecordClass Class => DurableRecordClass.LogStream;

    public async Task<IReadOnlyList<DurableRetentionCandidate>> ClaimAsync(DateTimeOffset now, DateTimeOffset terminalBefore, int limit, CancellationToken cancellationToken)
    {
        await using var db = CreateDb();

        return await db.AgentRunLogStream.AsNoTracking()
            .Where(stream => stream.State != AgentRunLogStreamState.Open && stream.PurgedAt == null
                && stream.CompletedAt != null && stream.CompletedAt <= terminalBefore
                && (stream.RetainUntil == null || stream.RetainUntil <= now))
            .OrderBy(stream => stream.CompletedAt).ThenBy(stream => stream.Id)
            .Take(limit)
            .Select(stream => new DurableRetentionCandidate(stream.Id, stream.TeamId, stream.Revision, stream.CompletedAt!.Value, stream.RetainUntil))
            .ToListAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Fail-closed: any failure to probe a citation site answers indeterminate, which every consumer reads as keep.</summary>
    public async Task<DurableReferenceVerdict> ClassifyAsync(DurableRetentionCandidate candidate, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(candidate);

        try
        {
            await using var db = CreateDb();

            if (await IsPinnedAsync(db, candidate.Id, cancellationToken).ConfigureAwait(false)) return DurableReferenceVerdict.Referenced;

            return await SharesBytesAsync(db, candidate, cancellationToken).ConfigureAwait(false)
                ? DurableReferenceVerdict.Referenced
                : DurableReferenceVerdict.Unreferenced;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Log stream {StreamId}: citation sites could not be probed; the stream is kept", candidate.Id);

            return DurableReferenceVerdict.Indeterminate;
        }
    }

    public async Task<bool> SettleAsync(DurableRetentionCandidate candidate, DurableRetentionDecision decision, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(decision);

        if (decision.Action == DurableRetentionAction.Quarantine) return await StampAsync(candidate, decision.RetainUntil, null, cancellationToken).ConfigureAwait(false);
        if (decision.Action != DurableRetentionAction.Collect) return false;

        return await CollectAsync(candidate, decision, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Bytes first, then the tombstone. A drain that did not finish defers the stream instead of stamping it, so the claim query stops returning it every hour.</summary>
    private async Task<bool> CollectAsync(DurableRetentionCandidate candidate, DurableRetentionDecision decision, CancellationToken cancellationToken)
    {
        var drained = await PurgeBytesAsync(candidate, cancellationToken).ConfigureAwait(false);
        var now = await DatabaseClockAsync(cancellationToken).ConfigureAwait(false);

        if (!drained)
        {
            await StampAsync(candidate, now.Add(DurableRetentionPolicy.LogStream.QuarantineWindow), null, cancellationToken).ConfigureAwait(false);

            return false;
        }

        var stamped = await StampAsync(candidate, decision.RetainUntil, now, cancellationToken).ConfigureAwait(false);

        if (stamped)
            _logger.LogInformation("Log stream {StreamId} purged for team {TeamId}: its segment bytes were reclaimed and the head row now reads purged", candidate.Id, candidate.TeamId);

        return stamped;
    }

    /// <summary>
    /// Removes every object this stream's segments still name, bounded per sweep. True only when NOTHING is left to
    /// remove — the tombstone must not claim more than the destination actually gave up.
    /// </summary>
    private async Task<bool> PurgeBytesAsync(DurableRetentionCandidate candidate, CancellationToken cancellationToken)
    {
        var objectIds = await ReclaimableObjectsAsync(candidate, MaxObjectsPerSweep + 1, cancellationToken).ConfigureAwait(false);

        if (objectIds.Count == 0) return true;

        foreach (var objectId in objectIds.Take(MaxObjectsPerSweep))
        {
            if (!await PurgeObjectAsync(candidate, objectId, cancellationToken).ConfigureAwait(false)) return false;
        }

        return objectIds.Count <= MaxObjectsPerSweep;
    }

    private async Task<bool> PurgeObjectAsync(DurableRetentionCandidate candidate, Guid objectId, CancellationToken cancellationToken)
    {
        var request = new ArtifactCasPurgeRequest { TeamId = candidate.TeamId, ArtifactObjectId = objectId, ActorId = SystemUsers.SeederId };
        var outcome = await _purge.PurgeAsync(request, cancellationToken).ConfigureAwait(false);

        if (outcome is ArtifactCasPurgeResult.Purged) return true;

        var rejected = (ArtifactCasPurgeResult.Rejected)outcome;
        _logger.LogWarning("Log stream {StreamId}: object {ObjectId} was not reclaimed ('{Problem}'); the stream keeps its bytes and its head row", candidate.Id, objectId, rejected.Problem.Code);

        return false;
    }

    /// <summary>
    /// The objects whose bytes are still somewhere, read inside the collecting pass rather than carried from the
    /// decision: a location the previous partial drain already purged is not asked about twice, which is what makes a
    /// resumed drain converge.
    /// </summary>
    private async Task<IReadOnlyList<Guid>> ReclaimableObjectsAsync(DurableRetentionCandidate candidate, int limit, CancellationToken cancellationToken)
    {
        await using var db = CreateDb();

        return await db.AgentRunLogSegment.AsNoTracking()
            .Where(segment => segment.TeamId == candidate.TeamId && segment.StreamId == candidate.Id)
            .Select(segment => segment.ArtifactObjectId)
            .Distinct()
            .Where(objectId => db.ArtifactLocation.Any(location => location.TeamId == candidate.TeamId && location.ArtifactObjectId == objectId
                && location.State != ArtifactLocationState.Purged && location.State != ArtifactLocationState.Deleted))
            .OrderBy(objectId => objectId)
            .Take(limit)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<bool> IsPinnedAsync(CodeSpaceDbContext db, Guid streamId, CancellationToken cancellationToken) =>
        await db.PairedQualificationResultPin.AsNoTracking()
            .AnyAsync(pin => pin.Kind == DurablePinKind.LogStream && pin.PinnedId == streamId, cancellationToken).ConfigureAwait(false);

    /// <summary>
    /// Whether anything else holds the same physical bytes. The CAS addresses an object by content, so two streams
    /// that captured identical bytes are ONE object with two names, and removing it for one takes the other's content
    /// too. Keeping is always safe here; collecting is not.
    /// </summary>
    private static async Task<bool> SharesBytesAsync(CodeSpaceDbContext db, DurableRetentionCandidate candidate, CancellationToken cancellationToken)
    {
        var objects = db.AgentRunLogSegment.AsNoTracking()
            .Where(segment => segment.TeamId == candidate.TeamId && segment.StreamId == candidate.Id)
            .Select(segment => segment.ArtifactObjectId);

        if (await db.AgentRunLogSegment.AsNoTracking().AnyAsync(segment => segment.StreamId != candidate.Id && objects.Contains(segment.ArtifactObjectId), cancellationToken).ConfigureAwait(false))
            return true;

        return await db.WorkflowArtifact.AsNoTracking()
            .AnyAsync(artifact => artifact.CasArtifactObjectId != null && objects.Contains(artifact.CasArtifactObjectId.Value), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The one write this cursor makes, and the only shape migration 0236's guard admits: the two retention columns,
    /// a revision that advances, nothing else. The revision is also the fence — a stream another worker moved since
    /// the claim matches nothing, and that sweep simply settles nothing.
    /// </summary>
    private async Task<bool> StampAsync(DurableRetentionCandidate candidate, DateTimeOffset? retainUntil, DateTimeOffset? purgedAt, CancellationToken cancellationToken)
    {
        var now = purgedAt ?? await DatabaseClockAsync(cancellationToken).ConfigureAwait(false);
        await using var db = CreateDb();

        var updated = await db.AgentRunLogStream
            .Where(stream => stream.Id == candidate.Id && stream.TeamId == candidate.TeamId && stream.Revision == candidate.Revision && stream.PurgedAt == null)
            .ExecuteUpdateAsync(set => set
                .SetProperty(stream => stream.RetainUntil, retainUntil)
                .SetProperty(stream => stream.PurgedAt, purgedAt)
                .SetProperty(stream => stream.Revision, stream => stream.Revision + 1)
                .SetProperty(stream => stream.LastModifiedAt, now), cancellationToken).ConfigureAwait(false);

        return updated == 1;
    }

    private CodeSpaceDbContext CreateDb() => new(_dbOptions);

    private async Task<DateTimeOffset> DatabaseClockAsync(CancellationToken cancellationToken)
    {
        await using var db = CreateDb();

        return await db.Database.SqlQueryRaw<DateTimeOffset>("SELECT clock_timestamp() AS \"Value\"").SingleAsync(cancellationToken).ConfigureAwait(false);
    }
}
