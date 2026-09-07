using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Messages.Enums;
using Microsoft.EntityFrameworkCore;

namespace CodeSpace.Core.Services.Workflows.Budget;

/// <summary>The budget reservation states (W-hard). Stored as text; live = Reserved|InFlight|Indeterminate — the states that hold cap headroom.</summary>
public static class BudgetReservationStates
{
    public const string Reserved = "Reserved";
    public const string InFlight = "InFlight";
    public const string Settled = "Settled";
    public const string Released = "Released";
    public const string Expired = "Expired";
    public const string Indeterminate = "Indeterminate";
    public const string Reconciled = "Reconciled";

    public static readonly IReadOnlyList<string> Live = new[] { Reserved, InFlight, Indeterminate };
}

public sealed record BudgetAdmission(bool Admitted, Guid? ReservationId, decimal CommittedUsd, decimal CapUsd, string? Reason)
{
    /// <summary>A lookup of an existing logical claim, never permission for a second physical provider request.</summary>
    public bool IsReplay { get; init; }
    public string? ReservationState { get; init; }
}

public interface IBudgetLedger
{
    /// <summary>Atomically reserve an estimate under the cap, serialized per run. This bounds admission commitments; it bounds the eventual provider bill only when the estimate is a trustworthy upper bound. Idempotent for the same run, team and reservation identity.</summary>
    Task<BudgetAdmission> ReserveAsync(Guid workflowRunId, Guid teamId, string kind, string scopeKey, decimal estimateUsd, decimal capUsd, string priceVersion, Guid? parentReservationId, DateTimeOffset? expiresAt, CancellationToken cancellationToken);

    /// <summary>Record known actual spend exactly. Null actual keeps the reserved claim and records Indeterminate, never an invented bill. A later known receipt supersedes uncertain or released bookkeeping; a confirmed settlement is idempotent.</summary>
    Task SettleAsync(Guid workflowRunId, Guid teamId, string kind, string scopeKey, decimal? actualUsd, CancellationToken cancellationToken);

    /// <summary>Release an unused reservation (the attempt never ran) — its headroom returns to the cap. Idempotent; a settled reservation is never released.</summary>
    Task ReleaseAsync(Guid workflowRunId, Guid teamId, string kind, string scopeKey, CancellationToken cancellationToken);

    /// <summary>Expire live reservations past their deadline (the orphan-recovery sweep): Reserved/InFlight → Indeterminate when a settlement may still surface, pessimistically HOLDING their headroom until reconciled. Returns how many moved.</summary>
    Task<int> ExpireOverdueAsync(int batchSize, CancellationToken cancellationToken);

    /// <summary>The run's committed total: settled + live reserved — what the invariant compares against the cap.</summary>
    Task<decimal> CommittedUsdAsync(Guid workflowRunId, Guid teamId, CancellationToken cancellationToken);

    /// <summary>
    /// W-hard slice 2: pessimistically reconcile a KIND PREFIX's dangling reservations — INDETERMINATE rows (the
    /// expiry sweep's output), and still-live rows whose run is terminal without an in-band settlement.
    /// Each lands <see cref="BudgetReservationStates.Reconciled"/> while its actual remains null and the reserve
    /// continues to hold headroom. This closes recovery bookkeeping without claiming a confirmed bill. Cap math is unchanged in the only direction that matters (headroom is never silently freed);
    /// what this closes is the FOREVER-LIVE orphan an in-flight teardown leaves behind.
    /// </summary>
    Task<int> ReconcileDanglingAsync(string kindPrefix, int batchSize, CancellationToken cancellationToken);
}

/// <summary>
/// Serializes admission, settlement and release per run. Unknown cost retains its reserved estimate without
/// presenting that estimate as actual spend. Conditional writes prevent stale tracked entities and recovery
/// sweeps from overwriting a provider receipt. Reservations are estimates unless their caller proves an upper bound.
/// </summary>
public sealed class BudgetLedger : IBudgetLedger, IScopedDependency
{
    private readonly CodeSpaceDbContext _db;

    public BudgetLedger(CodeSpaceDbContext db) => _db = db;

    public async Task<BudgetAdmission> ReserveAsync(Guid workflowRunId, Guid teamId, string kind, string scopeKey, decimal estimateUsd, decimal capUsd, string priceVersion, Guid? parentReservationId, DateTimeOffset? expiresAt, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(estimateUsd);
        ArgumentOutOfRangeException.ThrowIfNegative(capUsd);
        await using var tx = await _db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        await TakeRunLockAsync(workflowRunId, cancellationToken).ConfigureAwait(false);

        var existing = await _db.BudgetReservation.AsNoTracking()
            .Where(r => r.WorkflowRunId == workflowRunId && r.Kind == kind && r.ScopeKey == scopeKey)
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);

        if (existing is not null)
        {
            var sameTeam = existing.TeamId == teamId;
            var matches = sameTeam && existing.CapUsd == capUsd && existing.ReservedUsd == estimateUsd && existing.PriceVersion == priceVersion && existing.ParentReservationId == parentReservationId && DatabaseTimestamp(existing.ExpiresAt) == DatabaseTimestamp(expiresAt);
            var total = await CommittedInTxAsync(workflowRunId, teamId, cancellationToken).ConfigureAwait(false);
            await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new BudgetAdmission(matches, sameTeam ? existing.Id : null, total, capUsd, matches ? "already-reserved" : "reservation-intent-mismatch") { IsReplay = true, ReservationState = sameTeam ? existing.State : null };
        }

        var committed = await CommittedInTxAsync(workflowRunId, teamId, cancellationToken).ConfigureAwait(false);

        if (committed + estimateUsd > capUsd)
        {
            await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new BudgetAdmission(false, null, committed, capUsd, $"admission would commit {committed + estimateUsd:F4} past the {capUsd:F4} cap");
        }

        var reservation = new BudgetReservation
        {
            Id = Guid.NewGuid(),
            TeamId = teamId,
            WorkflowRunId = workflowRunId,
            ParentReservationId = parentReservationId,
            Kind = kind,
            ScopeKey = scopeKey,
            State = BudgetReservationStates.Reserved,
            ReservedUsd = estimateUsd,
            CapUsd = capUsd,
            PriceVersion = priceVersion,
            ExpiresAt = expiresAt,
        };

        _db.BudgetReservation.Add(reservation);
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);

        return new BudgetAdmission(true, reservation.Id, committed + estimateUsd, capUsd, null) { ReservationState = BudgetReservationStates.Reserved };
    }

    public async Task SettleAsync(Guid workflowRunId, Guid teamId, string kind, string scopeKey, decimal? actualUsd, CancellationToken cancellationToken)
    {
        if (actualUsd is < 0) throw new ArgumentOutOfRangeException(nameof(actualUsd));
        await using var tx = await _db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await TakeRunLockAsync(workflowRunId, cancellationToken).ConfigureAwait(false);
        var query = _db.BudgetReservation.Where(r => r.WorkflowRunId == workflowRunId && r.TeamId == teamId && r.Kind == kind && r.ScopeKey == scopeKey);

        if (actualUsd is { } actual)
        {
            // A real receipt is stronger evidence than an earlier claim that an attempt never ran. In particular,
            // settle-after-release must record money already spent, while release-after-settle must be a no-op.
            await query.Where(r => r.State != BudgetReservationStates.Settled)
                .ExecuteUpdateAsync(setters => setters.SetProperty(r => r.State, BudgetReservationStates.Settled).SetProperty(r => r.SettledUsd, actual).SetProperty(r => r.LastModifiedDate, DateTimeOffset.UtcNow), cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await query.Where(r => r.State != BudgetReservationStates.Settled)
                .ExecuteUpdateAsync(setters => setters.SetProperty(r => r.State, r => r.State == BudgetReservationStates.Reconciled ? BudgetReservationStates.Reconciled : BudgetReservationStates.Indeterminate).SetProperty(r => r.SettledUsd, (decimal?)null).SetProperty(r => r.LastModifiedDate, DateTimeOffset.UtcNow), cancellationToken).ConfigureAwait(false);
        }

        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task ReleaseAsync(Guid workflowRunId, Guid teamId, string kind, string scopeKey, CancellationToken cancellationToken)
    {
        await using var tx = await _db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await TakeRunLockAsync(workflowRunId, cancellationToken).ConfigureAwait(false);
        await _db.BudgetReservation.Where(r => r.WorkflowRunId == workflowRunId && r.TeamId == teamId && r.Kind == kind && r.ScopeKey == scopeKey && (r.State == BudgetReservationStates.Reserved || r.State == BudgetReservationStates.InFlight))
            .ExecuteUpdateAsync(setters => setters.SetProperty(r => r.State, BudgetReservationStates.Released).SetProperty(r => r.LastModifiedDate, DateTimeOffset.UtcNow), cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<int> ExpireOverdueAsync(int batchSize, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var overdue = _db.BudgetReservation.Where(r => (r.State == BudgetReservationStates.Reserved || r.State == BudgetReservationStates.InFlight) && r.ExpiresAt != null && r.ExpiresAt < now);
        var ids = await overdue.OrderBy(r => r.ExpiresAt).Select(r => r.Id).Take(batchSize).ToListAsync(cancellationToken).ConfigureAwait(false);

        // Re-evaluate the eligible state in the UPDATE, after any competing row lock. Never write a stale EF entity.
        return await overdue.Where(r => ids.Contains(r.Id))
            .ExecuteUpdateAsync(setters => setters.SetProperty(r => r.State, BudgetReservationStates.Indeterminate).SetProperty(r => r.LastModifiedDate, DateTimeOffset.UtcNow), cancellationToken).ConfigureAwait(false);
    }

    public async Task<decimal> CommittedUsdAsync(Guid workflowRunId, Guid teamId, CancellationToken cancellationToken) =>
        await _db.BudgetReservation.AsNoTracking()
            .Where(r => r.WorkflowRunId == workflowRunId && r.TeamId == teamId && r.State != BudgetReservationStates.Released && r.State != BudgetReservationStates.Expired)
            .SumAsync(r => r.SettledUsd ?? r.ReservedUsd, cancellationToken).ConfigureAwait(false);

    private async Task<decimal> CommittedInTxAsync(Guid workflowRunId, Guid teamId, CancellationToken cancellationToken) =>
        await _db.BudgetReservation
            .Where(r => r.WorkflowRunId == workflowRunId && r.TeamId == teamId && r.State != BudgetReservationStates.Released && r.State != BudgetReservationStates.Expired)
            .SumAsync(r => r.SettledUsd ?? r.ReservedUsd, cancellationToken).ConfigureAwait(false);

    public async Task<int> ReconcileDanglingAsync(string kindPrefix, int batchSize, CancellationToken cancellationToken)
    {
        var terminal = new[] { WorkflowRunStatus.Success, WorkflowRunStatus.Failure, WorkflowRunStatus.Cancelled };

        var dangling = _db.BudgetReservation.Where(r => r.Kind.StartsWith(kindPrefix)
            && (r.State == BudgetReservationStates.Indeterminate
                || ((r.State == BudgetReservationStates.Reserved || r.State == BudgetReservationStates.InFlight)
                    && _db.WorkflowRun.Any(w => w.Id == r.WorkflowRunId && terminal.Contains(w.Status)))));
        var ids = await dangling.OrderBy(r => r.CreatedDate).Select(r => r.Id).Take(batchSize).ToListAsync(cancellationToken).ConfigureAwait(false);

        // Reconciled closes recovery bookkeeping, not billing uncertainty. The nullable actual stays unknown;
        // committed arithmetic still counts ReservedUsd and a late actual receipt remains admissible.
        return await dangling.Where(r => ids.Contains(r.Id))
            .ExecuteUpdateAsync(setters => setters.SetProperty(r => r.State, BudgetReservationStates.Reconciled).SetProperty(r => r.SettledUsd, (decimal?)null).SetProperty(r => r.LastModifiedDate, DateTimeOffset.UtcNow), cancellationToken).ConfigureAwait(false);
    }

    // Npgsql/PostgreSQL timestamps store microseconds. Normalize the request to that same precision before
    // comparing a replay; the original caller may retain a sub-microsecond .NET tick that cannot round-trip.
    private static DateTimeOffset? DatabaseTimestamp(DateTimeOffset? value) => value is { } time ? new DateTimeOffset(time.UtcTicks - time.UtcTicks % 10, TimeSpan.Zero) : null;

    /// <summary>Serialize admission, settlement and release per run — pg_advisory_xact_lock releases with the transaction.</summary>
    private async Task TakeRunLockAsync(Guid workflowRunId, CancellationToken cancellationToken) =>
        await _db.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock(hashtextextended({workflowRunId.ToString()}, 42))", cancellationToken).ConfigureAwait(false);
}
