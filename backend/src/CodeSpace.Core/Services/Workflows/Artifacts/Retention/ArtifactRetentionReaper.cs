using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Workflows.Artifacts.Runtime;
using CodeSpace.Messages.Artifacts;
using CodeSpace.Messages.Constants;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CodeSpace.Core.Services.Workflows.Artifacts.Retention;

/// <summary>
/// Database-fenced collection of DECLARED artifacts that nothing references. Claims are short, each declaration is
/// settled under a proven owner/fence, and the reference question is re-asked inside the deleting transaction.
///
/// <para>Four properties are structural rather than checked. (1) Only a declared artifact is ever a candidate, so every
/// row written before the retention ledger existed and every byte the JSON offload paths write is out of reach. (2) The
/// claim query filters on the artifact's own age against the SMALLEST floor any class declares, and
/// <see cref="ArtifactRetentionDecision.Decide"/> then re-checks the claimed row's own class floor — so a just-written
/// object is neither claimed nor collectable.
/// (3) Collection additionally requires the quarantine window to have elapsed since the FIRST observation of
/// "unreferenced", which is a second, independent wait. (4) Every answer that is not a definite "no reference exists"
/// keeps the artifact — an unregistered class, an unreadable reference site, an exhausted retry budget and bytes with
/// no purge path all settle as <see cref="ArtifactRetentionState.Indeterminate"/>, which is terminal and means keep.
/// </para>
///
/// <para><b>Offloaded bytes.</b> An artifact past the inline threshold is collected too, but only on the local blob
/// backend and only when no other <c>workflow_artifact</c> row points at the same physical file — that path is addressed
/// by SHA alone and is NOT tenant-scoped, so one file can be shared by rows in different teams. Routed bytes use their
/// durable CAS location lifecycle: claim and provider I/O happen outside the metadata transaction, then the final
/// transaction revalidates references and removes the pointing row.</para>
///
/// <para><b>Order across the two media, and what a crash leaves.</b> The bytes go first and the row's DELETE commits
/// last. Local blob removal happens inside that final transaction; routed provider I/O cannot, so its Deleting/Purged
/// location revisions are the recoverable physical phase. A crash before the metadata commit leaves bytes gone and
/// the row plus its declaration intact — the next sweep observes Missing or Purged and finishes the row. The opposite
/// order would leak bytes no surviving row remembers. <c>workflow_artifact</c> itself remains immutable (migration
/// 0016); the purge lifecycle lives on the CAS location, never on that pointing row.</para>
///
/// <para><b>The routed soft-reference window.</b> The final oracle check is not allowed to make a late reference to
/// already-purged bytes look safe. The admission barrier is earlier: Deleting is not a reusable CAS location, so a
/// producer cannot obtain this artifact id from Put/dedup after the physical claim. Retention candidates have two
/// production minting callers, <c>ArtifactManifestStore</c> and <c>WorkflowSensitivePayloadStore</c>; each obtains the
/// id through <c>PutDeclaredAsync</c>, consumes it immediately in its own oracle-visible holder row — the manifest row
/// and the sensitive-payload sidecar row respectively — and does not return it. The sidecar's insert can be rolled back
/// by the transaction its caller owns while the declaration, minted on a scope of its own, cannot; that asymmetry is
/// deliberate and leaves exactly the declared-and-unreferenced artifact this sweep collects. A later recapture goes
/// through Put again and either revokes the declaration before the claim or is refused while Deleting. The
/// production-caller inventory and the post-claim mutation are pinned by tests; adding a caller that directly reuses a
/// candidate id would invalidate this protocol and must add equivalent admission before it can mint declarations.</para>
/// </summary>
public sealed class ArtifactRetentionReaper : IArtifactRetentionReaper
{
    private static readonly ArtifactRetentionReaperOptions Defaults =
        new(BatchSize: 200, ClaimSize: 25, MaxAttempts: 8, LeaseDuration: TimeSpan.FromSeconds(60), OperationTimeout: TimeSpan.FromSeconds(15), RetryDelay: TimeSpan.FromMinutes(30));

    private readonly DbContextOptions<CodeSpaceDbContext> _dbOptions;
    private readonly IArtifactReferenceOracle _oracle;

    /// <summary>The blob backend's optional removal capability, feature-detected once (Rule 7). Null is a supported state: every offloaded declaration the sweep claims then settles as a terminal keep instead.</summary>
    private readonly IArtifactBlobPurge? _purge;
    private readonly IArtifactCasPurgeCoordinator _routedPurge;

    private readonly ILogger<ArtifactRetentionReaper> _logger;
    private readonly ArtifactRetentionReaperOptions _options;

    public ArtifactRetentionReaper(DbContextOptions<CodeSpaceDbContext> dbOptions, IArtifactReferenceOracle oracle, IArtifactBlobBackend blobs,
        IArtifactCasPurgeCoordinator routedPurge, ILogger<ArtifactRetentionReaper> logger) : this(new ArtifactRetentionReaperServices(dbOptions, oracle, blobs, routedPurge, logger), Defaults) { }

    internal ArtifactRetentionReaper(ArtifactRetentionReaperServices services, ArtifactRetentionReaperOptions options)
    {
        if (options.BatchSize is <= 0 or > 2000 || options.ClaimSize is <= 0 || options.ClaimSize > options.BatchSize || options.MaxAttempts <= 0
            || options.LeaseDuration <= options.OperationTimeout || options.OperationTimeout <= TimeSpan.Zero || options.RetryDelay <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(options));
        _dbOptions = services.DbOptions;
        _oracle = services.Oracle;
        _purge = services.Blobs as IArtifactBlobPurge;
        _routedPurge = services.RoutedPurge;
        _logger = services.Logger;
        _options = options;
    }

    public async Task<ArtifactRetentionSweepSummary> SweepAsync(CancellationToken cancellationToken)
    {
        var ownerId = Guid.NewGuid();
        await using var clockDb = CreateDb();
        var cutoff = await DatabaseClockAsync(clockDb, cancellationToken).ConfigureAwait(false);
        var counts = new SweepCounts();

        while (counts.Claimed < _options.BatchSize)
        {
            var limit = Math.Min(_options.ClaimSize, _options.BatchSize - counts.Claimed);
            var claims = await ClaimBatchAsync(ownerId, cutoff, limit, cancellationToken).ConfigureAwait(false);

            if (claims.Count == 0) break;

            counts.Claimed += claims.Count;

            foreach (var claim in claims)
                counts.Record(await SweepClaimAsync(claim, cancellationToken).ConfigureAwait(false));
        }

        return counts.Summary();
    }

    /// <summary>
    /// Claims the per-team head of the live queue. <c>DISTINCT ON (team_id)</c> materialized first so one busy tenant
    /// cannot starve the others, the <c>LIMIT</c> on the locking select so <c>SKIP LOCKED</c> can backfill from later
    /// tenants, and <c>cutoff</c> frozen at sweep start so a settled row cannot be re-read within the same sweep.
    ///
    /// <para>The coarse <c>created_at</c> guard lives HERE, in SQL: a row below the policy's smallest age floor is never
    /// claimed, and the exact per-class floor is re-checked per row. Placement is deliberately not a queue predicate;
    /// routed artifacts now have a fenced purge lifecycle, and an unsupported physical shape is classified only after
    /// its declaration is owned.</para>
    /// </summary>
    private async Task<IReadOnlyList<SweepClaim>> ClaimBatchAsync(Guid ownerId, DateTimeOffset cutoff, int limit, CancellationToken cancellationToken)
    {
        var ageCeiling = cutoff - ArtifactRetentionPolicy.MinimumAgeFloor;
        await using var db = CreateDb();
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        // The three concurrently-mutable guards are repeated on the OUTER select on purpose, and the duplication is
        // load-bearing rather than defensive: FOR UPDATE re-evaluates a locked row through EPQ against the quals of
        // the query it locked in, and quals inside a MATERIALIZED CTE are not among them. Without the repeat, a row
        // this sweep saw as Declared but a concurrent transaction settled terminally before the lock was granted
        // comes back anyway — and the claim then writes an owner and a lease onto a terminal row, which the state
        // CHECK refuses, killing the whole sweep rather than skipping one row. The artifact-side guards are not
        // repeated because created_at cannot change under this row: workflow_artifact rejects UPDATE outright
        // (migration 0016).
        var rows = await db.WorkflowArtifactRetention.FromSqlInterpolated($$"""
            WITH fair AS MATERIALIZED (
                SELECT DISTINCT ON (retention.team_id) retention.artifact_id, retention.team_id, retention.next_sweep_at
                FROM workflow_artifact_retention retention
                JOIN workflow_artifact artifact ON artifact.id = retention.artifact_id AND artifact.team_id = retention.team_id
                WHERE retention.state IN ('Declared', 'Quarantined')
                  AND retention.next_sweep_at <= {{cutoff}}
                  AND (retention.lease_expires_at IS NULL OR retention.lease_expires_at <= clock_timestamp())
                  AND artifact.created_at <= {{ageCeiling}}
                ORDER BY retention.team_id, retention.next_sweep_at, retention.artifact_id
            )
            SELECT retention.*, retention.xmin FROM workflow_artifact_retention retention
            JOIN fair ON fair.artifact_id = retention.artifact_id
            WHERE retention.state IN ('Declared', 'Quarantined')
              AND retention.next_sweep_at <= {{cutoff}}
              AND (retention.lease_expires_at IS NULL OR retention.lease_expires_at <= clock_timestamp())
            ORDER BY retention.next_sweep_at, retention.team_id, retention.artifact_id
            LIMIT {{limit}}
            FOR UPDATE OF retention SKIP LOCKED
            """).ToListAsync(cancellationToken).ConfigureAwait(false);
        var now = await DatabaseClockAsync(db, cancellationToken).ConfigureAwait(false);

        foreach (var row in rows)
        {
            row.OwnerId = ownerId;
            row.FenceEpoch++;
            row.LeaseExpiresAt = now.Add(_options.LeaseDuration);
            row.Revision++;
            row.LastModifiedAt = now;
        }

        if (rows.Count > 0) await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        return rows.Select(SweepClaim.From).ToArray();
    }

    /// <summary>One claim, start to finish: bounded evaluation outside any long transaction, then a settlement that proves it still owns the lease.</summary>
    private async Task<SweepSettlement> SweepClaimAsync(SweepClaim claim, CancellationToken cancellationToken)
    {
        SweepEvaluation evaluation;
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        operation.CancelAfter(_options.OperationTimeout);

        try
        {
            evaluation = await EvaluateAsync(claim, operation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (OperationCanceledException) when (operation.IsCancellationRequested)
        {
            evaluation = new SweepEvaluation(ArtifactRetentionDecision.Retry("sweep-operation-timeout", "The bounded retention evaluation timed out."), null);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Artifact {ArtifactId}: retention evaluation raised an unexpected error; the artifact is kept", claim.ArtifactId);
            evaluation = new SweepEvaluation(ArtifactRetentionDecision.Retry("sweep-operation-exception", "The retention evaluation raised an unexpected error."), null);
        }

        if (evaluation.Outcome.Action == ArtifactRetentionAction.Collect && evaluation.RoutedObjectId is { } routedObjectId)
            return await SweepRoutedClaimAsync(claim, routedObjectId, cancellationToken).ConfigureAwait(false);

        using var settlement = new CancellationTokenSource(_options.OperationTimeout);

        try
        {
            return await SettleAsync(claim, evaluation.Outcome, settlement.Token).ConfigureAwait(false);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested) { return SweepSettlement.Lost; }
    }

    /// <summary>
    /// Gathers what one decision depends on and hands it to <see cref="ArtifactRetentionDecision.Decide"/>. Reads only:
    /// nothing here deletes, so a timeout or a throw costs only a re-queue.
    /// </summary>
    private async Task<SweepEvaluation> EvaluateAsync(SweepClaim claim, CancellationToken cancellationToken)
    {
        await using var db = CreateDb();
        var placement = await PlacementAsync(db, claim, cancellationToken).ConfigureAwait(false);

        if (placement is null)
            return new SweepEvaluation(ArtifactRetentionDecision.Retry("artifact-vanished", "The declared artifact was not readable at evaluation time."), null);

        var now = await DatabaseClockAsync(db, cancellationToken).ConfigureAwait(false);
        var verdict = await _oracle.ClassifyAsync(db, claim.ArtifactId, cancellationToken).ConfigureAwait(false);
        var observation = new ArtifactRetentionObservation(claim.State, placement.CreatedAt, placement.Purge, claim.QuarantinedAt, verdict, now);

        return new SweepEvaluation(ArtifactRetentionDecision.Decide(ArtifactRetentionPolicy.For(claim.RetentionClass), observation), placement.RoutedObjectId);
    }

    /// <summary>
    /// Runs the routed two-phase delete without holding a database transaction over provider I/O. The CAS claim is the
    /// physical fence; the retention claim is independently revalidated once after that fence and once after Purged,
    /// in the same transaction that removes <c>workflow_artifact</c> and its cascading declaration.
    /// </summary>
    private async Task<SweepSettlement> SweepRoutedClaimAsync(SweepClaim claim, Guid objectId, CancellationToken cancellationToken)
    {
        // One placement per sweep, named explicitly. An unnamed claim means "the only one", which an object placed at
        // several destinations cannot answer — and guessing would delete bytes nobody asked about. Naming the next
        // one makes the drain a sequence of independently fenced steps, each resumable from the ledger rather than
        // from a variable, because the declaration stays live until every placement is gone.
        var target = await NextUnpurgedPlacementAsync(claim.TeamId, objectId, cancellationToken).ConfigureAwait(false);

        if (target == null) return await SettleRoutedAsync(claim, ArtifactRetentionDecision.Collect(), cancellationToken).ConfigureAwait(false);

        ArtifactCasPurgeClaimResult physical;
        try
        {
            physical = await _routedPurge.ClaimAsync(new ArtifactCasPurgeRequest
            {
                TeamId = claim.TeamId, ArtifactObjectId = objectId, ActorId = SystemUsers.SeederId,
                ArtifactLocationId = target, OperationTimeout = _options.OperationTimeout,
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Artifact {ArtifactId}: routed purge claim failed; the live declaration will retry", claim.ArtifactId);
            return await SettleRoutedAsync(claim, RoutedWait("artifact-routed-claim-exception", "The routed location claim failed before a delete result was available."), cancellationToken).ConfigureAwait(false);
        }

        // Purged means the placement this sweep named was drained by someone else in between, not that the object is
        // done. Collecting here would delete the pointer while sibling destinations still hold bytes nothing could
        // reach afterwards — the reaper's only entry to an object is a declaration joined to workflow_artifact, so
        // the row IS the handle. Whether the object is finished is re-asked at the top of the next sweep.
        if (physical is ArtifactCasPurgeClaimResult.Purged)
            return await SettleRoutedAsync(claim, RoutedWait("artifact-routed-placement-already-purged",
                "The placement this sweep named was already drained; the live declaration continues with the next one."), cancellationToken).ConfigureAwait(false);

        if (physical is ArtifactCasPurgeClaimResult.Rejected rejected)
            return await SettleRoutedAsync(claim, ClaimRejection(rejected.Problem), cancellationToken).ConfigureAwait(false);

        var claimed = ((ArtifactCasPurgeClaimResult.Claimed)physical).Claim;
        return await SweepClaimedRoutedAsync(claim, objectId, claimed, cancellationToken).ConfigureAwait(false);
    }

    private async Task<Guid?> NextUnpurgedPlacementAsync(Guid teamId, Guid objectId, CancellationToken cancellationToken)
    {
        await using var db = CreateDb();

        return await db.ArtifactLocation.AsNoTracking()
            .Where(location => location.TeamId == teamId && location.ArtifactObjectId == objectId
                && location.State != ArtifactLocationState.Purged && location.State != ArtifactLocationState.Deleted)
            .OrderBy(location => location.Id)
            .Select(location => (Guid?)location.Id)
            .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<SweepSettlement> SweepClaimedRoutedAsync(SweepClaim claim, Guid objectId, ArtifactCasPurgeClaim physical, CancellationToken cancellationToken)
    {
        RoutedAuthorization authorization;
        try
        {
            using var verify = new CancellationTokenSource(_options.OperationTimeout);
            authorization = await AuthorizeRoutedDeleteAsync(claim, objectId, verify.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "Artifact {ArtifactId}: post-claim reference verification failed; provider delete was not attempted", claim.ArtifactId);
            var released = await ReleaseRoutedAsync(physical).ConfigureAwait(false);
            var verification = ArtifactRetentionDecision.Retry("artifact-routed-pre-delete-verification", "The post-claim reference verification failed before provider I/O.");

            return await SettleRoutedAsync(claim, AfterRelease(released, verification, "The physical claim changed while the safe release was attempted."), cancellationToken).ConfigureAwait(false);
        }

        if (authorization.LostLease)
        {
            // Discarded deliberately: this sweep no longer owns the declaration, so it settles nothing and has no
            // outcome to carry the distinction into. Whoever holds the lease re-asks all of it.
            await ReleaseRoutedAsync(physical).ConfigureAwait(false);
            return SweepSettlement.Lost;
        }

        if (authorization.Outcome.Action != ArtifactRetentionAction.Collect)
        {
            var released = await ReleaseRoutedAsync(physical).ConfigureAwait(false);

            return await SettleRoutedAsync(claim, AfterRelease(released, authorization.Outcome, "The physical claim changed while a stopped delete was being released."), cancellationToken).ConfigureAwait(false);
        }

        ArtifactCasPurgeResult deletion;
        try
        {
            deletion = await _routedPurge.DeleteAsync(physical, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Artifact {ArtifactId}: routed provider delete has an uncertain result; the durable Deleting claim will be reconciled", claim.ArtifactId);
            return await SettleRoutedAsync(claim, RoutedWait("artifact-routed-delete-exception", "The provider delete result is uncertain; a later sweep will reconcile the durable physical claim."), cancellationToken).ConfigureAwait(false);
        }

        if (deletion is ArtifactCasPurgeResult.Purged)
            return await SettleRoutedAsync(claim, ArtifactRetentionDecision.Collect(), cancellationToken).ConfigureAwait(false);

        var failed = (ArtifactCasPurgeResult.Rejected)deletion;
        if (failed.EffectMayHaveOccurred)
            return await SettleRoutedAsync(claim, RoutedWait("artifact-routed-delete-uncertain", $"The provider delete returned '{failed.Problem.Code}' after an effect may have occurred; a later sweep will reconcile it."), cancellationToken).ConfigureAwait(false);

        var safeRelease = await ReleaseRoutedAsync(physical).ConfigureAwait(false);
        var refused = ArtifactRetentionDecision.Retry("artifact-routed-delete-refused", $"The provider refused routed deletion with '{failed.Problem.Code}' before any effect; the location was released.");

        return await SettleRoutedAsync(claim, AfterRelease(safeRelease, refused, "The physical claim changed before a no-effect delete could release it."), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The post-location-claim gate. Its transaction ends before the provider call; no provider I/O can inherit this row lock.</summary>
    private async Task<RoutedAuthorization> AuthorizeRoutedDeleteAsync(SweepClaim claim, Guid objectId, CancellationToken cancellationToken)
    {
        await using var db = CreateDb();
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var row = await db.WorkflowArtifactRetention.FromSqlInterpolated(
            $"SELECT workflow_artifact_retention.*, xmin FROM workflow_artifact_retention WHERE artifact_id = {claim.ArtifactId} FOR UPDATE")
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        var now = await DatabaseClockAsync(db, cancellationToken).ConfigureAwait(false);
        if (!StillOwned(row, claim, now)) return RoutedAuthorization.Lost;

        var placement = await PlacementAsync(db, claim, cancellationToken).ConfigureAwait(false);
        var outcome = placement?.RoutedObjectId == objectId
            ? await ReverifyAsync(db, claim, cancellationToken).ConfigureAwait(false)
            : ArtifactRetentionDecision.Retry("artifact-routed-placement-changed", "The pointing row no longer names the object claimed for routed deletion.");
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new RoutedAuthorization(outcome, false);
    }

    private async Task<SweepSettlement> SettleRoutedAsync(SweepClaim claim, ArtifactRetentionDecision outcome, CancellationToken cancellationToken)
    {
        using var settlement = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        settlement.CancelAfter(_options.OperationTimeout);
        try { return await SettleAsync(claim, outcome, settlement.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception) { return SweepSettlement.Lost; }
    }

    /// <summary>
    /// Hands the physical claim back. The reaper never inspects the destination while holding it, so the evidence is
    /// always <see cref="ArtifactCasReleaseEvidence.Untouched"/> and a claim taken from a <c>Deleting</c> orphan has
    /// no resting state to be put back into.
    ///
    /// <para>A release that THREW is a bad moment, not a barren path: the database was unreachable, and the next
    /// sweep asks a world that has moved. It reports <see cref="ArtifactCasReleaseOutcome.Raced"/> so infrastructure
    /// never spends the budget reserved for a call that can only ever fail.</para>
    /// </summary>
    private async Task<ArtifactCasReleaseOutcome> ReleaseRoutedAsync(ArtifactCasPurgeClaim claim)
    {
        using var cleanup = new CancellationTokenSource(_options.OperationTimeout);
        try { return await _routedPurge.ReleaseAsync(claim, ArtifactCasReleaseEvidence.Untouched, cleanup.Token).ConfigureAwait(false); }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Routed location {LocationId}: release failed; a later sweep will reconcile Deleting", claim.LocationId);
            return ArtifactCasReleaseOutcome.Raced;
        }
    }

    /// <summary>
    /// What the sweep settles once the physical claim has been handed back, given what the hand-back could establish.
    ///
    /// <para>The distinction is the whole point of the three-way answer. A race is the design working and must NOT be
    /// budgeted: the next sweep meets a row this one never held, so waiting costs nothing and spending an attempt on
    /// it would exhaust the allowance reserved for real failures. An orphaned claim is the opposite — the identical
    /// call has the identical answer forever, so it MUST be budgeted or the declaration is re-claimed every retry
    /// delay for good, bumping the placement's revision each time and never reaching a terminal state.</para>
    /// </summary>
    private ArtifactRetentionDecision AfterRelease(ArtifactCasReleaseOutcome outcome, ArtifactRetentionDecision released, string racedMessage) => outcome switch
    {
        ArtifactCasReleaseOutcome.Released => released,
        ArtifactCasReleaseOutcome.Raced => RoutedWait("artifact-routed-release-race", racedMessage),
        _ => ArtifactRetentionDecision.Retry("artifact-routed-release-orphaned-claim",
            "The placement was claimed from a Deleting marker an earlier worker left behind, so releasing it can establish no state to put it back into. "
            + "This sweep cannot close it at all; the profile drain (ProfileAbandonmentService) is its only exit, and the declaration is kept once the attempts run out."),
    };

    private ArtifactRetentionDecision RoutedWait(string code, string message) => ArtifactRetentionDecision.WaitForRetry(code, message);

    private static ArtifactRetentionDecision ClaimRejection(ArtifactCasProblem problem) =>
        ArtifactRetentionDecision.Retry("artifact-routed-claim-refused", $"The routed location claim was refused with '{problem.Code}'.");

    /// <summary>
    /// Where the artifact's bytes are and whether they can be removed, read through <paramref name="db"/> so the
    /// collector asks inside its own transaction. Null means the row is gone.
    ///
    /// <para>The sharing probe is the one that keeps a tenant's bytes safe: <c>LocalFileArtifactBlobBackend</c> addresses
    /// a blob by SHA with no team in the path, so a row in ANOTHER team holding identical content points at the same
    /// file. The probe is deliberately not team-scoped, and it compares the <c>storage_url</c> rather than the digest
    /// because the url IS the pointer at the file — migration 0148 indexes it for exactly this.</para>
    ///
    /// <para>The window this probe does NOT close: a concurrent first write of identical content in another team can
    /// insert its row after this read and before the collector commits, and that row is then left naming a file the
    /// collector removed. Ordering the two would need the artifact write path to hold a lock across its blob write and
    /// its row insert, which this lane does not change. The affected read fails loudly rather than returning wrong
    /// bytes, and the next write of that content by that team restores the blob through the dedup-hit self-heal in
    /// <c>ArtifactStore.WriteAsync</c>.</para>
    /// </summary>
    private async Task<ArtifactPlacement?> PlacementAsync(CodeSpaceDbContext db, SweepClaim claim, CancellationToken cancellationToken)
    {
        var artifact = await db.WorkflowArtifact.AsNoTracking()
            .Where(row => row.Id == claim.ArtifactId && row.TeamId == claim.TeamId)
            .Select(row => new { row.CreatedAt, row.StorageUrl, row.CasArtifactObjectId })
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);

        if (artifact is null) return null;
        if (artifact.CasArtifactObjectId is { } objectId)
            return new ArtifactPlacement(artifact.CreatedAt, await RoutedPurgePathAsync(db, objectId, cancellationToken).ConfigureAwait(false), null, objectId);

        // Neither routed nor a storage_url means the bytes are in the row: 0016's storage xor was validated when it
        // required exactly one of inline_bytes/storage_url, so a row with no destination at all cannot exist.
        if (artifact.StorageUrl is not { } storageUrl) return new ArtifactPlacement(artifact.CreatedAt, ArtifactPurgePath.Inline, null, null);

        if (_purge is null) return new ArtifactPlacement(artifact.CreatedAt, ArtifactPurgePath.BackendCannotPurge, null, null);

        var shared = await db.WorkflowArtifact.AsNoTracking()
            .AnyAsync(other => other.StorageUrl == storageUrl && other.Id != claim.ArtifactId, cancellationToken).ConfigureAwait(false);

        return shared
            ? new ArtifactPlacement(artifact.CreatedAt, ArtifactPurgePath.LocalBlobShared, null, null)
            : new ArtifactPlacement(artifact.CreatedAt, ArtifactPurgePath.LocalBlobExclusive, storageUrl, null);
    }


    /// <summary>
    /// The routed arm of the same sharing question the local arm asks below, and it has to be asked for the same
    /// reason: <c>ArtifactStore.ObjectKeyFor</c> builds <c>workflow-artifacts/{aa}/{bb}/{sha256}</c> with NO team
    /// segment, so two objects holding identical content land on the same key. Whether that key is the same PHYSICAL
    /// object then depends entirely on whether the two profile revisions name the same namespace — which is what
    /// <c>storage_profile_revision.namespace_fingerprint</c> identifies, and the only thing that identifies it.
    ///
    /// <para>Comparing the object key ALONE would be unsound in the safe direction but useless in practice: every team
    /// storing the same bytes shares a key, so the reaper would refuse to collect any deduplicated content even when
    /// the two teams are in different buckets. Comparing the fingerprint alone would be unsound in the other
    /// direction. Both together are the question "is this the same object", which is the one the local arm answers
    /// with a <c>storage_url</c> comparison because there the url already carries both.</para>
    ///
    /// <para>Deliberately NOT team-scoped, and deliberately not filtered by <see cref="ArtifactLocationState"/>. A
    /// location whose bytes are already Purged cannot really be harmed, so including it only ever costs a kept
    /// artifact, never a lost one; excluding it would make the probe depend on a lifecycle race it cannot observe.
    /// Keeping is always safe here, collecting is not.</para>
    ///
    /// <para>The window this probe does NOT close is the same one the local arm names: a concurrent first placement of
    /// identical content into a shared namespace can insert its location after this read and before the collector
    /// commits. Closing it would need the placement path to hold a lock across its transfer and its location insert,
    /// which this lane does not change.</para>
    /// </summary>
    private static async Task<ArtifactPurgePath> RoutedPurgePathAsync(CodeSpaceDbContext db, Guid objectId, CancellationToken cancellationToken)
    {
        var mine = await db.ArtifactLocation.AsNoTracking()
            .Where(location => location.ArtifactObjectId == objectId)
            .Join(db.StorageProfileRevision.AsNoTracking(), location => location.StorageProfileRevisionId, revision => revision.Id,
                (location, revision) => new PlacedAt(location.ObjectKey, revision.NamespaceFingerprint))
            .Distinct()
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        if (mine.Count == 0) return ArtifactPurgePath.Routed;

        var keys = mine.Select(placed => placed.ObjectKey).Distinct().ToList();

        var collisions = await db.ArtifactLocation.AsNoTracking()
            .Where(location => location.ArtifactObjectId != objectId && keys.Contains(location.ObjectKey))
            .Join(db.StorageProfileRevision.AsNoTracking(), location => location.StorageProfileRevisionId, revision => revision.Id,
                (location, revision) => new PlacedAt(location.ObjectKey, revision.NamespaceFingerprint))
            .Distinct()
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        return collisions.Any(mine.Contains) ? ArtifactPurgePath.RoutedObjectShared : ArtifactPurgePath.Routed;
    }

    /// <summary>One physical destination: the key, plus the namespace identity that says WHICH store that key is inside.</summary>
    private sealed record PlacedAt(string ObjectKey, string NamespaceFingerprint);

    /// <summary>
    /// Applies the outcome under a proven claim. The COLLECT arm additionally re-asks the reference question inside this
    /// very transaction, after taking the declaration's row lock — which orders the check after every reference a writer
    /// has already committed, and orders the store's revoke either strictly before it (so we read <c>Revoked</c> and keep)
    /// or strictly after our commit.
    /// </summary>
    private async Task<SweepSettlement> SettleAsync(SweepClaim claim, ArtifactRetentionDecision outcome, CancellationToken cancellationToken)
    {
        await using var db = CreateDb();
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        // The table's DELETE trigger rejects every purge that has not said so in its own session (migration 0016). SET
        // LOCAL scopes the permission to THIS transaction, so a rollback or any other connection can never inherit it.
        if (outcome.Action == ArtifactRetentionAction.Collect)
            await db.Database.ExecuteSqlRawAsync("SET LOCAL codespace.artifact_purge_allowed = on", cancellationToken).ConfigureAwait(false);

        var row = await db.WorkflowArtifactRetention.FromSqlInterpolated(
            $"SELECT workflow_artifact_retention.*, xmin FROM workflow_artifact_retention WHERE artifact_id = {claim.ArtifactId} FOR UPDATE")
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        var now = await DatabaseClockAsync(db, cancellationToken).ConfigureAwait(false);

        if (!StillOwned(row, claim, now)) return SweepSettlement.Lost;

        var settled = outcome.Action == ArtifactRetentionAction.Collect
            ? await CollectAsync(db, claim, cancellationToken).ConfigureAwait(false)
            : outcome;

        if (settled.Action == ArtifactRetentionAction.Collect && !await DeleteArtifactAsync(db, claim, cancellationToken).ConfigureAwait(false))
            return SweepSettlement.Lost;

        if (settled.Action != ArtifactRetentionAction.Collect) Apply(row!, settled, now);

        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

            return new SweepSettlement(settled.Action, false);
        }
        catch (DbUpdateConcurrencyException) { return SweepSettlement.Lost; }
    }

    /// <summary>
    /// Everything the deletion depends on, re-established inside the deleting transaction, and then the byte removal
    /// that the row's DELETE must not outrun. Four gates in order, each of which abandons the deletion: the reference
    /// question, the placement question, whether any placement still holds bytes, and the backend's own answer.
    /// </summary>
    private async Task<ArtifactRetentionDecision> CollectAsync(CodeSpaceDbContext db, SweepClaim claim, CancellationToken cancellationToken)
    {
        var reference = await ReverifyAsync(db, claim, cancellationToken).ConfigureAwait(false);

        if (reference.Action != ArtifactRetentionAction.Collect) return reference;

        var placement = await PlacementAsync(db, claim, cancellationToken).ConfigureAwait(false);

        if (placement is null)
            return ArtifactRetentionDecision.Retry("artifact-vanished-at-collection", "The declared artifact was not readable inside the collecting transaction.");

        if (ArtifactRetentionDecision.RefuseUnpurgeable(placement.Purge) is { } unpurgeable) return unpurgeable;

        if (placement.RoutedObjectId is { } routedObjectId && await HoldsBytesElsewhereAsync(db, claim.TeamId, routedObjectId, cancellationToken).ConfigureAwait(false))
            return ArtifactRetentionDecision.Retry("artifact-routed-placement-remaining",
                "A placement of this object still holds bytes at its destination; deleting the row now would leave them with nothing that can ever reach them.");

        return placement.StorageUrl is { } storageUrl
            ? await PurgeBytesAsync(claim, storageUrl, cancellationToken).ConfigureAwait(false)
            : ArtifactRetentionDecision.Collect();
    }

    /// <summary>
    /// Whether any placement of this object is still holding bytes at a destination.
    ///
    /// <para>Re-asked here, inside the collecting transaction, rather than trusted from the sweep that led here: the
    /// row about to be deleted is the reaper's ONLY handle on the object — its entry is always a declaration joined
    /// to <c>workflow_artifact</c> — so a placement that outlives the row is bytes nothing can ever reach again, and
    /// <c>artifact_location</c> rows are never deleted at all. Until this gate existed the guarantee came from the
    /// purge claim refusing every multi-placed object outright, which bought it at the price of never purging one.</para>
    /// </summary>
    private static async Task<bool> HoldsBytesElsewhereAsync(CodeSpaceDbContext db, Guid teamId, Guid objectId, CancellationToken cancellationToken) =>
        await db.ArtifactLocation.AsNoTracking()
            .AnyAsync(location => location.TeamId == teamId && location.ArtifactObjectId == objectId
                && location.State != ArtifactLocationState.Purged && location.State != ArtifactLocationState.Deleted, cancellationToken)
            .ConfigureAwait(false);

    /// <summary>
    /// Removes the offloaded bytes before the row that names them. A refusal is a budgeted
    /// <see cref="ArtifactRetentionAction.Retry"/>, which rolls the whole transaction back — so a backend that will not
    /// delete leaves the bytes, the row and a live declaration exactly as they were.
    /// </summary>
    private async Task<ArtifactRetentionDecision> PurgeBytesAsync(SweepClaim claim, string storageUrl, CancellationToken cancellationToken)
    {
        // Non-null by construction: a non-null StorageUrl is only ever produced on the LocalBlobExclusive line of
        // PlacementAsync, which the null check immediately above it guards.
        var outcome = await _purge!.DeleteAsync(storageUrl, cancellationToken).ConfigureAwait(false);

        if (outcome == ArtifactBlobPurgeOutcome.Refused)
            return ArtifactRetentionDecision.Retry("artifact-blob-delete-refused", "The blob backend refused to remove the artifact's bytes, so neither the bytes nor the row were removed.");

        _logger.LogInformation("Artifact {ArtifactId}: offloaded bytes for team {TeamId} settled as {PurgeOutcome} before the row delete", claim.ArtifactId, claim.TeamId, outcome);

        return ArtifactRetentionDecision.Collect();
    }

    /// <summary>The fail-closed second look, inside the deleting transaction. Anything other than a definite "unreferenced" abandons the deletion.</summary>
    private async Task<ArtifactRetentionDecision> ReverifyAsync(CodeSpaceDbContext db, SweepClaim claim, CancellationToken cancellationToken)
    {
        var verdict = await _oracle.ClassifyAsync(db, claim.ArtifactId, cancellationToken).ConfigureAwait(false);

        if (verdict == ArtifactReferenceVerdict.Referenced) return ArtifactRetentionDecision.Referenced();

        return verdict == ArtifactReferenceVerdict.Unreferenced
            ? ArtifactRetentionDecision.Collect()
            : ArtifactRetentionDecision.Retry("reference-status-indeterminate-at-collection", "A reference site could not be probed inside the collecting transaction.");
    }

    /// <summary>
    /// The single DELETE. Team-scoped as well as id-scoped so a corrupted claim cannot reach another tenant's row, and
    /// zero rows affected is the IDEMPOTENT case, not an error: a concurrent reaper already collected it.
    /// </summary>
    private async Task<bool> DeleteArtifactAsync(CodeSpaceDbContext db, SweepClaim claim, CancellationToken cancellationToken)
    {
        var deleted = await db.Database.ExecuteSqlInterpolatedAsync(
            $"DELETE FROM workflow_artifact WHERE id = {claim.ArtifactId} AND team_id = {claim.TeamId}", cancellationToken).ConfigureAwait(false);

        if (deleted == 1)
            _logger.LogInformation("Artifact {ArtifactId} collected for team {TeamId} under retention class {RetentionClass} declared by {HolderKind} {HolderId}",
                claim.ArtifactId, claim.TeamId, claim.RetentionClass, claim.HolderKind, claim.HolderId);

        return deleted == 1;
    }

    /// <summary>
    /// Writes the settled state onto the claimed row. A budgeted retry that has run out of attempts becomes
    /// <see cref="ArtifactRetentionState.Indeterminate"/> — terminal, and it means the artifact is kept. A
    /// <see cref="ArtifactRetentionAction.Wait"/> is NOT budgeted: waiting out an age floor or a quarantine window is the design
    /// working, so it must never spend the allowance reserved for genuine failures.
    /// </summary>
    private void Apply(WorkflowArtifactRetention row, ArtifactRetentionDecision outcome, DateTimeOffset now)
    {
        var exhausted = outcome.Action == ArtifactRetentionAction.Retry && row.AttemptCount + 1 >= _options.MaxAttempts;
        var action = exhausted ? ArtifactRetentionAction.Indeterminate : outcome.Action;

        row.State = action switch
        {
            ArtifactRetentionAction.Quarantine => ArtifactRetentionState.Quarantined,
            ArtifactRetentionAction.Referenced => ArtifactRetentionState.Referenced,
            ArtifactRetentionAction.Indeterminate => ArtifactRetentionState.Indeterminate,
            _ => row.State,
        };
        row.QuarantinedAt = action == ArtifactRetentionAction.Quarantine ? now : row.QuarantinedAt;
        row.TerminalAt = row.State is ArtifactRetentionState.Referenced or ArtifactRetentionState.Indeterminate ? now : null;
        row.NextSweepAt = outcome.NextSweepAt ?? now.Add(_options.RetryDelay);
        row.AttemptCount += outcome.Action == ArtifactRetentionAction.Retry ? 1 : 0;
        row.LastErrorCode = exhausted ? "retention-sweep-exhausted" : outcome.ErrorCode;
        row.LastErrorMessage = exhausted ? $"Retention sweeps stopped after {_options.MaxAttempts} attempts on '{outcome.ErrorCode}'; the artifact is kept." : outcome.ErrorMessage;
        row.OwnerId = null;
        row.LeaseExpiresAt = null;
        row.Revision++;
        row.LastModifiedAt = now;
    }

    private static bool StillOwned(WorkflowArtifactRetention? row, SweepClaim claim, DateTimeOffset now) =>
        row is not null && row.OwnerId == claim.OwnerId && row.FenceEpoch == claim.FenceEpoch && row.LeaseExpiresAt > now && row.State == claim.State;

    private CodeSpaceDbContext CreateDb() => new(_dbOptions);

    private static Task<DateTimeOffset> DatabaseClockAsync(CodeSpaceDbContext db, CancellationToken cancellationToken) =>
        db.Database.SqlQueryRaw<DateTimeOffset>("SELECT clock_timestamp() AS \"Value\"").SingleAsync(cancellationToken);

    private sealed record SweepSettlement(ArtifactRetentionAction Action, bool LostLease)
    {
        public static SweepSettlement Lost { get; } = new(ArtifactRetentionAction.Retry, true);
    }

    private sealed record SweepEvaluation(ArtifactRetentionDecision Outcome, Guid? RoutedObjectId);

    private sealed record RoutedAuthorization(ArtifactRetentionDecision Outcome, bool LostLease)
    {
        public static RoutedAuthorization Lost { get; } = new(ArtifactRetentionDecision.Retry("retention-lease-lost", "The retention claim was no longer owned."), true);
    }

    /// <summary>
    /// One artifact's storage facts as one read saw them. <c>StorageUrl</c> is non-null for
    /// <see cref="ArtifactPurgePath.LocalBlobExclusive"/> and nothing else, which is what makes it safe to read as
    /// "there are bytes here to delete, and a backend that will delete them" — every other placement carries null
    /// however its bytes are actually stored.
    /// </summary>
    private sealed record ArtifactPlacement(DateTimeOffset CreatedAt, ArtifactPurgePath Purge, string? StorageUrl, Guid? RoutedObjectId);

    /// <summary>
    /// The claimed row AS CLAIMED — an in-memory snapshot, never re-read. Settlement compares a freshly loaded row
    /// against these values to prove it is still the same claim, so the snapshot must not be a live tracked entity.
    /// </summary>
    private sealed record SweepClaim(WorkflowArtifactRetention Claimed)
    {
        public Guid ArtifactId => Claimed.ArtifactId;
        public Guid TeamId => Claimed.TeamId;
        public string RetentionClass => Claimed.RetentionClass;
        public string HolderKind => Claimed.HolderKind;
        public Guid HolderId => Claimed.HolderId;
        public ArtifactRetentionState State => Claimed.State;
        public DateTimeOffset? QuarantinedAt => Claimed.QuarantinedAt;
        public Guid OwnerId => Claimed.OwnerId!.Value;
        public long FenceEpoch => Claimed.FenceEpoch;

        public static SweepClaim From(WorkflowArtifactRetention row) => new(row);
    }

    private sealed class SweepCounts
    {
        public int Claimed { get; set; }
        private int Quarantined { get; set; }
        private int Collected { get; set; }
        private int Referenced { get; set; }
        private int Indeterminate { get; set; }
        private int Retried { get; set; }
        private int LostLease { get; set; }

        public void Record(SweepSettlement settlement)
        {
            if (settlement.LostLease) LostLease++;
            else if (settlement.Action == ArtifactRetentionAction.Quarantine) Quarantined++;
            else if (settlement.Action == ArtifactRetentionAction.Collect) Collected++;
            else if (settlement.Action == ArtifactRetentionAction.Referenced) Referenced++;
            else if (settlement.Action == ArtifactRetentionAction.Indeterminate) Indeterminate++;
            else Retried++;
        }

        public ArtifactRetentionSweepSummary Summary() => new()
        {
            Claimed = Claimed, Quarantined = Quarantined, Collected = Collected,
            Referenced = Referenced, Indeterminate = Indeterminate, Retried = Retried, LostLease = LostLease,
        };
    }
}

internal sealed record ArtifactRetentionReaperServices(DbContextOptions<CodeSpaceDbContext> DbOptions, IArtifactReferenceOracle Oracle,
    IArtifactBlobBackend Blobs, IArtifactCasPurgeCoordinator RoutedPurge, ILogger<ArtifactRetentionReaper> Logger);

internal sealed record ArtifactRetentionReaperOptions(int BatchSize, int ClaimSize, int MaxAttempts, TimeSpan LeaseDuration, TimeSpan OperationTimeout, TimeSpan RetryDelay);
