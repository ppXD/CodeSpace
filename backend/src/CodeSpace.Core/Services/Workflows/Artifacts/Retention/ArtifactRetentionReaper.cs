using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Messages.Artifacts;
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
/// keeps the artifact — an unregistered class, an unreadable reference site, an exhausted retry budget and a
/// non-inline row all settle as <see cref="ArtifactRetentionState.Indeterminate"/>, which is terminal and means keep.
/// </para>
/// </summary>
public sealed class ArtifactRetentionReaper : IArtifactRetentionReaper
{
    private static readonly ArtifactRetentionReaperOptions Defaults =
        new(BatchSize: 200, ClaimSize: 25, MaxAttempts: 8, LeaseDuration: TimeSpan.FromSeconds(60), OperationTimeout: TimeSpan.FromSeconds(15), RetryDelay: TimeSpan.FromMinutes(30));

    private readonly DbContextOptions<CodeSpaceDbContext> _dbOptions;
    private readonly IArtifactReferenceOracle _oracle;
    private readonly ILogger<ArtifactRetentionReaper> _logger;
    private readonly ArtifactRetentionReaperOptions _options;

    public ArtifactRetentionReaper(DbContextOptions<CodeSpaceDbContext> dbOptions, IArtifactReferenceOracle oracle, ILogger<ArtifactRetentionReaper> logger) : this(dbOptions, oracle, logger, Defaults) { }

    internal ArtifactRetentionReaper(DbContextOptions<CodeSpaceDbContext> dbOptions, IArtifactReferenceOracle oracle, ILogger<ArtifactRetentionReaper> logger, ArtifactRetentionReaperOptions options)
    {
        if (options.BatchSize is <= 0 or > 2000 || options.ClaimSize is <= 0 || options.ClaimSize > options.BatchSize || options.MaxAttempts <= 0
            || options.LeaseDuration <= options.OperationTimeout || options.OperationTimeout <= TimeSpan.Zero || options.RetryDelay <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(options));
        _dbOptions = dbOptions;
        _oracle = oracle;
        _logger = logger;
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
    /// <para>Two of the safety properties are enforced HERE, in SQL, not downstream: <c>created_at</c> below the policy's
    /// smallest age floor is never claimed (the exact per-class floor is re-checked per row), and a row whose artifact is
    /// not inline is never claimed at all, because no purge path exists for offloaded bytes.</para>
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
        // repeated because neither created_at nor inline_bytes can change under this row.
        var rows = await db.WorkflowArtifactRetention.FromSqlInterpolated($$"""
            WITH fair AS MATERIALIZED (
                SELECT DISTINCT ON (retention.team_id) retention.artifact_id, retention.team_id, retention.next_sweep_at
                FROM workflow_artifact_retention retention
                JOIN workflow_artifact artifact ON artifact.id = retention.artifact_id AND artifact.team_id = retention.team_id
                WHERE retention.state IN ('Declared', 'Quarantined')
                  AND retention.next_sweep_at <= {{cutoff}}
                  AND (retention.lease_expires_at IS NULL OR retention.lease_expires_at <= clock_timestamp())
                  AND artifact.created_at <= {{ageCeiling}}
                  AND artifact.inline_bytes IS NOT NULL
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
        ArtifactRetentionDecision outcome;
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        operation.CancelAfter(_options.OperationTimeout);

        try
        {
            outcome = await EvaluateAsync(claim, operation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (OperationCanceledException) when (operation.IsCancellationRequested)
        {
            outcome = ArtifactRetentionDecision.Retry("sweep-operation-timeout", "The bounded retention evaluation timed out.");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Artifact {ArtifactId}: retention evaluation raised an unexpected error; the artifact is kept", claim.ArtifactId);
            outcome = ArtifactRetentionDecision.Retry("sweep-operation-exception", "The retention evaluation raised an unexpected error.");
        }

        using var settlement = new CancellationTokenSource(_options.OperationTimeout);

        try
        {
            return await SettleAsync(claim, outcome, settlement.Token).ConfigureAwait(false);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested) { return SweepSettlement.Lost; }
    }

    /// <summary>
    /// Gathers what one decision depends on and hands it to <see cref="ArtifactRetentionDecision.Decide"/>. Reads only:
    /// nothing here deletes, so a timeout or a throw costs only a re-queue.
    /// </summary>
    private async Task<ArtifactRetentionDecision> EvaluateAsync(SweepClaim claim, CancellationToken cancellationToken)
    {
        await using var db = CreateDb();
        var artifact = await db.WorkflowArtifact.AsNoTracking()
            .Where(row => row.Id == claim.ArtifactId && row.TeamId == claim.TeamId)
            .Select(row => new { row.CreatedAt, Inline = row.InlineBytes != null })
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);

        if (artifact is null) return ArtifactRetentionDecision.Retry("artifact-vanished", "The declared artifact was not readable at evaluation time.");

        var now = await DatabaseClockAsync(db, cancellationToken).ConfigureAwait(false);
        var verdict = await _oracle.ClassifyAsync(db, claim.ArtifactId, cancellationToken).ConfigureAwait(false);
        var observation = new ArtifactRetentionObservation(claim.State, artifact.CreatedAt, artifact.Inline, claim.QuarantinedAt, verdict, now);

        return ArtifactRetentionDecision.Decide(ArtifactRetentionPolicy.For(claim.RetentionClass), observation);
    }

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
            ? await ReverifyAsync(db, claim, cancellationToken).ConfigureAwait(false)
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

internal sealed record ArtifactRetentionReaperOptions(int BatchSize, int ClaimSize, int MaxAttempts, TimeSpan LeaseDuration, TimeSpan OperationTimeout, TimeSpan RetryDelay);
