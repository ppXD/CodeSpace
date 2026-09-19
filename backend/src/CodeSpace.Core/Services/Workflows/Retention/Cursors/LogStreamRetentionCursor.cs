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
/// entire point of a retention plane is that reclamation is legible. The read path answers a purged stream with its
/// own typed code and the Room says purged.</para>
///
/// <para><b>Order, and what a crash leaves.</b> The bytes go first and the tombstone commits last. A crash in between
/// leaves bytes gone and no tombstone — the next sweep re-asks, finds every location already Purged, and finishes the
/// stamp; the drain is idempotent by construction. The opposite order would leave bytes that no surviving row
/// remembers how to reach, which is the one outcome nothing can repair.</para>
///
/// <para><b>What is never collected.</b> Anything <see cref="CitationSites"/> still names. And anything at all when
/// the question could not be asked: every failure answers <see cref="DurableReferenceVerdict.Indeterminate"/>, never
/// "unreferenced".</para>
///
/// <para><b>What this cursor cannot reclaim, and says so.</b> A REPLICATED object — one whose bytes sit at more than
/// one destination — cannot be purged at all: <c>IArtifactCasPurgeCoordinator</c> refuses it before touching any
/// location, because deleting every replica needs an object-level claim the schema does not yet represent. This
/// cursor names that case from the locations it has already read rather than from the refusal, since the coordinator
/// rejects it with the same code as "no location at all" and a caller that could not tell them apart would retry a
/// call whose answer can never change. Such a stream is kept and logged under its own message; a deployment that
/// replicates its log storage reclaims nothing here until that claim exists.</para>
/// </summary>
public sealed class LogStreamRetentionCursor : IDurableRetentionCursor, IScopedDependency
{
    /// <summary>
    /// Objects purged per stream per sweep. A 4 GiB capture is thousands of segments, and a sweep that tried to drain
    /// one in a single pass would hold a worker for minutes on provider I/O. A partial drain deliberately writes
    /// NOTHING, so the stream is re-claimed on the very next tick and continues where it stopped — unlike a refusal,
    /// which defers it for the class's recheck interval.
    /// </summary>
    internal const int MaxObjectsPerSweep = 64;

    /// <summary>
    /// Every place a log stream's CAS object can still be named, as <c>(what, why)</c>. This list IS the safety of the
    /// purge, exactly as <c>ArtifactReferenceOracle.ReferenceSites</c> is for artifacts: a citer missing from it makes
    /// the cursor answer "unreferenced" about bytes something still reaches, which is the one failure mode of this
    /// class that destroys data. Public and pinned by a test, so a new citer is a deliberate edit.
    /// </summary>
    public static readonly IReadOnlyList<(string Table, string Column)> CitationSites =
    [
        ("paired_qualification_result_pin", "pinned_id"),
        ("agent_run_log_segment", "artifact_object_id"),
        ("workflow_artifact", "cas_artifact_object_id"),
    ];

    /// <summary>
    /// The one column that names an artifact object and is deliberately NOT a citation site, recorded here because
    /// leaving it out silently is how a list like this rots. <c>artifact_transfer_intent.artifact_object_id</c> is
    /// null on every non-terminal row — <c>ck_artifact_transfer_intent_outcome</c> (migration 0127) requires it — so
    /// a saga in flight cannot name the object it is moving, and every row that DOES name one is a finished transfer
    /// whose record is history. Probing it would answer "still cited" about every log stream in the deployment for
    /// ever, because a transfer is exactly what wrote each segment, and it would look like the cursor working.
    /// </summary>
    public const string CommittedTransfersAreNotCiters = "artifact_transfer_intent.artifact_object_id";

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

    /// <summary>
    /// The per-team head of the eligible queue. <c>DISTINCT ON (team_id)</c> is materialized first so one tenant with
    /// a long backlog cannot starve the others, and the same guards are repeated on the outer select because quals
    /// inside a materialized CTE are not re-evaluated against a row another worker settled in between.
    /// </summary>
    public async Task<IReadOnlyList<DurableRetentionCandidate>> ClaimAsync(DurableRetentionSweepWindow window, int limit, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(window);
        await using var db = CreateDb();

        var rows = await db.AgentRunLogStream.FromSqlInterpolated($$"""
            WITH fair AS MATERIALIZED (
                SELECT DISTINCT ON (stream.team_id) stream.id, stream.completed_at
                FROM agent_run_log_stream stream
                WHERE stream.state <> 'Open' AND stream.purged_at IS NULL
                  AND stream.completed_at IS NOT NULL AND stream.completed_at <= {{window.TerminalBefore}}
                  AND stream.last_modified_at <= {{window.RecheckBefore}}
                ORDER BY stream.team_id, stream.completed_at, stream.id
            )
            SELECT stream.*, stream.xmin FROM agent_run_log_stream stream
            JOIN fair ON fair.id = stream.id
            WHERE stream.state <> 'Open' AND stream.purged_at IS NULL
              AND stream.completed_at IS NOT NULL AND stream.completed_at <= {{window.TerminalBefore}}
              AND stream.last_modified_at <= {{window.RecheckBefore}}
            ORDER BY stream.completed_at, stream.id
            LIMIT {{limit}}
            """).AsNoTracking().ToListAsync(cancellationToken).ConfigureAwait(false);

        return rows.Select(stream => new DurableRetentionCandidate(stream.Id, stream.TeamId, stream.Revision, stream.CompletedAt!.Value, stream.RetainUntil)).ToArray();
    }

    /// <summary>Fail-closed: any failure to probe a citation site answers indeterminate, which every consumer reads as keep.</summary>
    public async Task<DurableReferenceVerdict> ClassifyAsync(DurableRetentionCandidate candidate, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(candidate);

        try
        {
            await using var db = CreateDb();

            if (await IsPinnedAsync(db, candidate, cancellationToken).ConfigureAwait(false)) return DurableReferenceVerdict.Referenced;

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

        return decision.Action == DurableRetentionAction.Collect
            ? await CollectAsync(candidate, decision, cancellationToken).ConfigureAwait(false)
            : await StampAsync(candidate, decision.RetainUntil, null, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Bytes first, then the tombstone, and only for a stream this plane can account for.
    ///
    /// <para>The three non-collecting exits are deliberately different. A drain still making progress writes NOTHING,
    /// so the next tick continues it. A refusal defers, so one unreachable destination cannot own a batch slot. And a
    /// stream whose bytes are already gone by some OTHER path is never tombstoned: stamping it would make the Room
    /// say "purged; retention window elapsed" about a loss this plane did not cause, inverting the very distinction
    /// the tombstone exists to preserve.</para>
    /// </summary>
    private async Task<bool> CollectAsync(DurableRetentionCandidate candidate, DurableRetentionDecision decision, CancellationToken cancellationToken)
    {
        var drain = await DrainAsync(candidate, cancellationToken).ConfigureAwait(false);

        if (drain == DrainOutcome.Progressing) return false;

        if (drain != DrainOutcome.Drained)
        {
            await DeferAsync(candidate, cancellationToken).ConfigureAwait(false);

            return false;
        }

        var now = await DatabaseClockAsync(cancellationToken).ConfigureAwait(false);
        var stamped = await StampAsync(candidate, decision.RetainUntil, now, cancellationToken).ConfigureAwait(false);

        if (stamped)
            _logger.LogInformation("Log stream {StreamId} purged for team {TeamId}: its segment bytes were reclaimed and the head row now reads purged", candidate.Id, candidate.TeamId);

        return stamped;
    }

    /// <summary>
    /// Removes every object this stream's segments still name, bounded per sweep. Only <see cref="DrainOutcome.Drained"/>
    /// permits a tombstone, and it requires POSITIVE evidence that this plane's own lifecycle emptied the stream:
    /// segments were captured, and every object behind them now rests at <c>Purged</c>.
    /// </summary>
    private async Task<DrainOutcome> DrainAsync(DurableRetentionCandidate candidate, CancellationToken cancellationToken)
    {
        await using var db = CreateDb();
        var objects = Objects(db, candidate);

        if (!await objects.AnyAsync(cancellationToken).ConfigureAwait(false)) return NothingCaptured(candidate);

        var reclaimable = await ReclaimableAsync(db, candidate, objects, cancellationToken).ConfigureAwait(false);

        if (reclaimable.Count == 0) return await DrainedOrLostAsync(db, candidate, objects, cancellationToken).ConfigureAwait(false);
        if (reclaimable.FirstOrDefault(row => row.LiveLocations > 1) is { } replicated) return Replicated(candidate, replicated);

        foreach (var row in reclaimable.Take(MaxObjectsPerSweep))
        {
            if (!await PurgeObjectAsync(candidate, row.ObjectId, cancellationToken).ConfigureAwait(false)) return DrainOutcome.Refused;
        }

        return reclaimable.Count > MaxObjectsPerSweep ? DrainOutcome.Progressing : DrainOutcome.Drained;
    }

    /// <summary>The objects this stream's segments name — the entry point for every question the drain asks.</summary>
    private static IQueryable<Guid> Objects(CodeSpaceDbContext db, DurableRetentionCandidate candidate) =>
        db.AgentRunLogSegment.AsNoTracking()
            .Where(segment => segment.TeamId == candidate.TeamId && segment.StreamId == candidate.Id)
            .Select(segment => segment.ArtifactObjectId)
            .Distinct();

    /// <summary>
    /// The next objects whose bytes are still at a destination, each with how many destinations hold it, and one row
    /// over the batch so the caller can see whether another sweep is needed. Read inside the collecting pass rather
    /// than carried from the decision, so a resumed drain converges instead of re-asking about objects an earlier
    /// pass already took.
    /// </summary>
    private static async Task<IReadOnlyList<SegmentObject>> ReclaimableAsync(CodeSpaceDbContext db, DurableRetentionCandidate candidate, IQueryable<Guid> objects, CancellationToken cancellationToken)
    {
        // Projected through an anonymous type rather than straight into the record: a positional constructor inside a
        // GROUP BY projection is not translatable, and the exception it raises is swallowed as "could not be
        // evaluated" — which reads exactly like a fail-closed keep and hides a cursor that reclaims nothing.
        var rows = await db.ArtifactLocation.AsNoTracking()
            .Where(location => location.TeamId == candidate.TeamId && objects.Contains(location.ArtifactObjectId)
                && location.State != ArtifactLocationState.Purged && location.State != ArtifactLocationState.Deleted)
            .GroupBy(location => location.ArtifactObjectId)
            .Select(group => new { ObjectId = group.Key, LiveLocations = group.Count() })
            .OrderBy(row => row.ObjectId)
            .Take(MaxObjectsPerSweep + 1)
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        return rows.Select(row => new SegmentObject(row.ObjectId, row.LiveLocations)).ToArray();
    }

    /// <summary>
    /// Nothing is left to remove, so either this plane's own lifecycle emptied the stream or something else did. Only
    /// the first may be tombstoned: an object whose locations never reached <c>Purged</c> is a loss this cursor did
    /// not cause and must not claim. Counting the unaccounted objects over ALL of them, not the batch, is what keeps
    /// a stream with more objects than one sweep can hold from being declared drained on a partial view.
    /// </summary>
    private async Task<DrainOutcome> DrainedOrLostAsync(CodeSpaceDbContext db, DurableRetentionCandidate candidate, IQueryable<Guid> objects, CancellationToken cancellationToken)
    {
        var unaccounted = await objects.CountAsync(objectId => !db.ArtifactLocation
            .Any(location => location.TeamId == candidate.TeamId && location.ArtifactObjectId == objectId && location.State == ArtifactLocationState.Purged), cancellationToken).ConfigureAwait(false);

        return unaccounted == 0 ? DrainOutcome.Drained : BytesAlreadyGone(candidate, unaccounted);
    }

    /// <summary>
    /// Named here rather than read off a refusal code, because the coordinator cannot tell this refusal from "no
    /// location at all" — both reject with <c>ArtifactMissing</c>, and a cursor that guessed between them would keep
    /// retrying a call whose answer can never change. The locations this drain already read say it exactly.
    /// </summary>
    private DrainOutcome Replicated(DurableRetentionCandidate candidate, SegmentObject replicated)
    {
        _logger.LogWarning("Log stream {StreamId}: object {ObjectId} holds bytes at {LocationCount} destinations, which this plane cannot reclaim — deleting every replica needs an object-level purge claim that does not exist yet, so the stream is kept",
            candidate.Id, replicated.ObjectId, replicated.LiveLocations);

        return DrainOutcome.Replicated;
    }

    private DrainOutcome NothingCaptured(DurableRetentionCandidate candidate)
    {
        _logger.LogInformation("Log stream {StreamId}: no segment ever landed, so there are no bytes to reclaim and no purge to record", candidate.Id);

        return DrainOutcome.BytesAlreadyGone;
    }

    private DrainOutcome BytesAlreadyGone(DurableRetentionCandidate candidate, int unaccounted)
    {
        _logger.LogWarning("Log stream {StreamId}: {UnaccountedObjects} of its segment objects hold no reclaimable location and were never purged by this plane; the head row is NOT tombstoned, so the loss is not reported as a retention purge",
            candidate.Id, unaccounted);

        return DrainOutcome.BytesAlreadyGone;
    }

    private async Task<bool> PurgeObjectAsync(DurableRetentionCandidate candidate, Guid objectId, CancellationToken cancellationToken)
    {
        var request = new ArtifactCasPurgeRequest { TeamId = candidate.TeamId, ArtifactObjectId = objectId, ActorId = SystemUsers.SeederId };
        var outcome = await _purge.PurgeAsync(request, cancellationToken).ConfigureAwait(false);

        switch (outcome)
        {
            case ArtifactCasPurgeResult.Purged:
                return true;
            case ArtifactCasPurgeResult.Rejected rejected:
                _logger.LogWarning("Log stream {StreamId}: object {ObjectId} was not reclaimed ('{Problem}'); the stream keeps its bytes and its head row", candidate.Id, objectId, rejected.Problem.Code);
                return false;
            default:
                _logger.LogWarning("Log stream {StreamId}: object {ObjectId} returned an unrecognised purge outcome; the stream is kept", candidate.Id, objectId);
                return false;
        }
    }

    private static async Task<bool> IsPinnedAsync(CodeSpaceDbContext db, DurableRetentionCandidate candidate, CancellationToken cancellationToken) =>
        await db.PairedQualificationResultPin.AsNoTracking()
            .AnyAsync(pin => pin.Kind == DurablePinKind.LogStream && pin.PinnedId == candidate.Id, cancellationToken).ConfigureAwait(false);

    /// <summary>
    /// Whether anything else holds the same physical bytes. The CAS addresses an object by content, so two streams
    /// that captured identical bytes — the ordinary case for short or empty output — are ONE object with two names,
    /// and removing it for one takes the other's content too. Keeping is always safe here; collecting is not. For why
    /// a transfer intent is not asked, see <see cref="CommittedTransfersAreNotCiters"/>.
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

    /// <summary>A keep that could not be settled any other way still has to advance the row's modification time, or the claim query returns it again on the very next tick.</summary>
    private async Task<bool> DeferAsync(DurableRetentionCandidate candidate, CancellationToken cancellationToken) =>
        await StampAsync(candidate, candidate.RetainUntil, null, cancellationToken).ConfigureAwait(false);

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

    /// <summary>One segment object as one read saw it: how many destinations still hold its bytes, and whether any location rests at Purged.</summary>
    private sealed record SegmentObject(Guid ObjectId, int LiveLocations);

    /// <summary>What one drain attempt established. Only <see cref="Drained"/> permits a tombstone.</summary>
    private enum DrainOutcome
    {
        /// <summary>Every object this stream's segments name is gone through this plane's own purge lifecycle.</summary>
        Drained,

        /// <summary>Objects were removed and more remain. Write nothing: the next tick continues from here.</summary>
        Progressing,

        /// <summary>The destination would not give the bytes up. Defer.</summary>
        Refused,

        /// <summary>The bytes sit at more than one destination, which no claim in this schema can take. Defer, and say so.</summary>
        Replicated,

        /// <summary>Nothing was captured, or the bytes left by a path this plane did not drive. Never a purge.</summary>
        BytesAlreadyGone,
    }
}
