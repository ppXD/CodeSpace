using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
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

public sealed record BudgetAdmission(bool Admitted, Guid? ReservationId, decimal CommittedUsd, decimal CapUsd, string? Reason);

public interface IBudgetLedger
{
    /// <summary>Atomically admit-and-reserve under THE invariant (settled + live ≤ cap), serialized per run by an advisory lock — two concurrent waves can never jointly overshoot. Idempotent per (run, kind, scopeKey): a replay returns the existing reservation as admitted.</summary>
    Task<BudgetAdmission> ReserveAsync(Guid workflowRunId, Guid teamId, string kind, string scopeKey, decimal estimateUsd, decimal capUsd, string priceVersion, Guid? parentReservationId, DateTimeOffset? expiresAt, CancellationToken cancellationToken);

    /// <summary>Settle at the ACTUAL spend when known; PESSIMISTIC when not (null actual settles AT the reserved amount, never lower — only a later reconcile pass may adjust down). Idempotent: an already-settled reservation is a no-op.</summary>
    Task SettleAsync(Guid workflowRunId, Guid teamId, string kind, string scopeKey, decimal? actualUsd, CancellationToken cancellationToken);

    /// <summary>Release an unused reservation (the attempt never ran) — its headroom returns to the cap. Idempotent; a settled reservation is never released.</summary>
    Task ReleaseAsync(Guid workflowRunId, Guid teamId, string kind, string scopeKey, CancellationToken cancellationToken);

    /// <summary>Expire live reservations past their deadline (the orphan-recovery sweep): Reserved/InFlight → Indeterminate when a settlement may still surface, pessimistically HOLDING their headroom until reconciled. Returns how many moved.</summary>
    Task<int> ExpireOverdueAsync(int batchSize, CancellationToken cancellationToken);

    /// <summary>The run's committed total: settled + live reserved — what the invariant compares against the cap.</summary>
    Task<decimal> CommittedUsdAsync(Guid workflowRunId, Guid teamId, CancellationToken cancellationToken);
}

/// <summary>
/// W-hard (v4.2-FINAL): THE atomic budget ledger — the transactional inversion of the pure-fold realized-spend
/// check whose documented weakness is one-wave overshoot. Admission runs inside a DB transaction holding a
/// per-run advisory lock (<c>pg_advisory_xact_lock</c> over the run id's hash), so the invariant
/// <c>settled + live ≤ cap</c> is checked-and-committed atomically: concurrent reserves serialize, and the
/// second wave sees the first wave's headroom claim. Settlement is pessimistic; expiry never silently frees
/// headroom (an overdue reservation goes Indeterminate and HOLDS its claim until a reconcile pass decides).
/// </summary>
public sealed class BudgetLedger : IBudgetLedger, IScopedDependency
{
    private readonly CodeSpaceDbContext _db;

    public BudgetLedger(CodeSpaceDbContext db) => _db = db;

    public async Task<BudgetAdmission> ReserveAsync(Guid workflowRunId, Guid teamId, string kind, string scopeKey, decimal estimateUsd, decimal capUsd, string priceVersion, Guid? parentReservationId, DateTimeOffset? expiresAt, CancellationToken cancellationToken)
    {
        await using var tx = await _db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        await TakeRunLockAsync(workflowRunId, cancellationToken).ConfigureAwait(false);

        var existing = await _db.BudgetReservation
            .Where(r => r.WorkflowRunId == workflowRunId && r.Kind == kind && r.ScopeKey == scopeKey)
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);

        if (existing is not null)
        {
            await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new BudgetAdmission(true, existing.Id, await CommittedUsdAsync(workflowRunId, teamId, cancellationToken).ConfigureAwait(false), capUsd, "already-reserved");
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
            PriceVersion = priceVersion,
            ExpiresAt = expiresAt,
        };

        _db.BudgetReservation.Add(reservation);
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);

        return new BudgetAdmission(true, reservation.Id, committed + estimateUsd, capUsd, null);
    }

    public async Task SettleAsync(Guid workflowRunId, Guid teamId, string kind, string scopeKey, decimal? actualUsd, CancellationToken cancellationToken)
    {
        var reservation = await _db.BudgetReservation
            .Where(r => r.WorkflowRunId == workflowRunId && r.TeamId == teamId && r.Kind == kind && r.ScopeKey == scopeKey)
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);

        if (reservation is null || reservation.State is BudgetReservationStates.Settled or BudgetReservationStates.Reconciled or BudgetReservationStates.Released) return;

        reservation.State = BudgetReservationStates.Settled;
        // Pessimistic: an unknown actual settles AT the reserved amount, never lower.
        reservation.SettledUsd = actualUsd ?? reservation.ReservedUsd;
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task ReleaseAsync(Guid workflowRunId, Guid teamId, string kind, string scopeKey, CancellationToken cancellationToken)
    {
        var reservation = await _db.BudgetReservation
            .Where(r => r.WorkflowRunId == workflowRunId && r.TeamId == teamId && r.Kind == kind && r.ScopeKey == scopeKey)
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);

        if (reservation is null || reservation.State is not (BudgetReservationStates.Reserved or BudgetReservationStates.InFlight)) return;

        reservation.State = BudgetReservationStates.Released;
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<int> ExpireOverdueAsync(int batchSize, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var overdue = await _db.BudgetReservation
            .Where(r => (r.State == BudgetReservationStates.Reserved || r.State == BudgetReservationStates.InFlight) && r.ExpiresAt != null && r.ExpiresAt < now)
            .OrderBy(r => r.ExpiresAt)
            .Take(batchSize)
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        foreach (var reservation in overdue)
            reservation.State = BudgetReservationStates.Indeterminate;   // holds its headroom until a reconcile decides — expiry never silently frees the cap

        if (overdue.Count > 0) await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return overdue.Count;
    }

    public async Task<decimal> CommittedUsdAsync(Guid workflowRunId, Guid teamId, CancellationToken cancellationToken) =>
        await _db.BudgetReservation.AsNoTracking()
            .Where(r => r.WorkflowRunId == workflowRunId && r.TeamId == teamId && r.State != BudgetReservationStates.Released && r.State != BudgetReservationStates.Expired)
            .SumAsync(r => r.SettledUsd ?? r.ReservedUsd, cancellationToken).ConfigureAwait(false);

    private async Task<decimal> CommittedInTxAsync(Guid workflowRunId, Guid teamId, CancellationToken cancellationToken) =>
        await _db.BudgetReservation
            .Where(r => r.WorkflowRunId == workflowRunId && r.TeamId == teamId && r.State != BudgetReservationStates.Released && r.State != BudgetReservationStates.Expired)
            .SumAsync(r => r.SettledUsd ?? r.ReservedUsd, cancellationToken).ConfigureAwait(false);

    /// <summary>Serialize admissions per run — pg_advisory_xact_lock releases with the transaction.</summary>
    private async Task TakeRunLockAsync(Guid workflowRunId, CancellationToken cancellationToken) =>
        await _db.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock(hashtextextended({workflowRunId.ToString()}, 42))", cancellationToken).ConfigureAwait(false);
}
