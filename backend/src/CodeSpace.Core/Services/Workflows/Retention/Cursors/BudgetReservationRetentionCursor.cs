using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Services.Workflows.Budget;
using CodeSpace.Messages.Retention;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CodeSpace.Core.Services.Workflows.Retention.Cursors;

/// <summary>
/// Reclaims a terminal <c>budget_reservation</c> row that nothing cites any more.
///
/// <para><b>Never a live claim.</b> <see cref="BudgetReservationStates.Live"/> — Reserved, InFlight, Indeterminate —
/// is the set the ledger's own admission arithmetic counts as committed spend; a row in any of them is still holding
/// cap, and removing one would hand a team back money it has not finished spending. Only the four states nothing can
/// change again are candidates, and the claim names them rather than excluding the live ones, so a NEW state is
/// out of scope until someone puts it in.</para>
///
/// <para><b>Why ninety days outruns every reader.</b> A team cap's committed sum is measured over a rolling window,
/// and <c>TeamCostCap.RollingThirtyDays</c> is the only window the schema admits, so a row terminal for ninety days is
/// outside it three times over — and the floor is measured from the claim's own terminal instant, which is at or after
/// its creation, so the row is at least that old by the window's clock too. The Room reads a run's reservations as an
/// aggregate for the turn it is showing, and <c>BudgetSettlementService</c> selects LIVE rows of a run to settle; both
/// are by run, neither by id, and neither can reach a claim this old.</para>
///
/// <para><b>Deletion is admitted, and the database is the backstop.</b> The table carries no trigger, but two foreign
/// keys point AT it with <c>ON DELETE RESTRICT</c> — a physical model-call receipt, and a child reservation's parent.
/// This cursor probes both rather than letting the constraint raise: a refusal that arrives as an exception is a
/// sweep-wide error, while a citation that arrives as a verdict is one row kept, counted and logged.</para>
/// </summary>
public sealed class BudgetReservationRetentionCursor : IDurableRetentionCursor, IScopedDependency
{
    /// <summary>
    /// Every place a terminal reservation can still be cited, as <c>(what, why)</c>. Public and pinned by a test,
    /// because a citer missing from it makes the cursor propose deleting a row something still names — and although
    /// both of today's entries are also enforced by <c>ON DELETE RESTRICT</c>, a future citer without a constraint
    /// would have nothing but this list standing in front of it.
    /// </summary>
    public static readonly IReadOnlyList<(string Table, string Column)> CitationSites =
    [
        ("workflow_run_model_call_attempt", "budget_reservation_id"),
        ("budget_reservation", "parent_reservation_id"),
    ];

    /// <summary>The states nothing can change again. Deliberately an allow-list: a state added to the ledger is out of scope until it is named here, rather than in scope the moment it is not "live".</summary>
    internal static readonly string[] TerminalStates =
        [BudgetReservationStates.Settled, BudgetReservationStates.Released, BudgetReservationStates.Expired, BudgetReservationStates.Reconciled];

    private readonly DbContextOptions<CodeSpaceDbContext> _dbOptions;
    private readonly ILogger<BudgetReservationRetentionCursor> _logger;

    public BudgetReservationRetentionCursor(DbContextOptions<CodeSpaceDbContext> dbOptions, ILogger<BudgetReservationRetentionCursor> logger)
    {
        _dbOptions = dbOptions;
        _logger = logger;
    }

    public DurableRecordClass Class => DurableRecordClass.BudgetReservation;

    /// <summary>
    /// The per-team head of the eligible queue, oldest first, with both citers excluded here rather than settled — a
    /// reservation a model-call receipt names is cited for as long as that receipt exists, so leaving it claimable
    /// would fill the batch with rows no sweep can ever collect.
    /// </summary>
    public async Task<IReadOnlyList<DurableRetentionCandidate>> ClaimAsync(DurableRetentionSweepWindow window, int limit, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(window);
        await using var db = CreateDb();

        var rows = await db.BudgetReservation.FromSqlInterpolated($$"""
            WITH eligible AS MATERIALIZED (
                SELECT DISTINCT ON (claim.team_id) claim.id, claim.last_modified_date
                FROM budget_reservation claim
                WHERE claim.state IN ('Settled', 'Released', 'Expired', 'Reconciled')
                  AND claim.last_modified_date <= {{window.TerminalBefore}}
                  AND (claim.retain_until IS NULL OR claim.retain_until <= {{window.Now}})
                  AND NOT EXISTS (SELECT 1 FROM workflow_run_model_call_attempt attempt WHERE attempt.budget_reservation_id = claim.id)
                  AND NOT EXISTS (SELECT 1 FROM budget_reservation child WHERE child.parent_reservation_id = claim.id)
                ORDER BY claim.team_id, claim.last_modified_date, claim.id
            )
            SELECT claim.* FROM budget_reservation claim
            JOIN eligible ON eligible.id = claim.id
            WHERE claim.state IN ('Settled', 'Released', 'Expired', 'Reconciled')
              AND claim.last_modified_date <= {{window.TerminalBefore}}
              AND (claim.retain_until IS NULL OR claim.retain_until <= {{window.Now}})
            ORDER BY claim.last_modified_date, claim.id
            LIMIT {{limit}}
            """).AsNoTracking().ToListAsync(cancellationToken).ConfigureAwait(false);

        return rows.Select(claim => new DurableRetentionCandidate(claim.Id, claim.TeamId, 0, claim.LastModifiedDate, claim.RetainUntil)).ToArray();
    }

    /// <summary>Fail-closed: any failure to probe a citation site answers indeterminate, which every consumer reads as keep.</summary>
    public async Task<DurableReferenceVerdict> ClassifyAsync(DurableRetentionCandidate candidate, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(candidate);

        try
        {
            await using var db = CreateDb();

            if (await db.WorkflowRunModelCallAttempt.AsNoTracking().AnyAsync(attempt => attempt.BudgetReservationId == candidate.Id, cancellationToken).ConfigureAwait(false))
                return DurableReferenceVerdict.Referenced;

            return await db.BudgetReservation.AsNoTracking().AnyAsync(child => child.ParentReservationId == candidate.Id, cancellationToken).ConfigureAwait(false)
                ? DurableReferenceVerdict.Referenced
                : DurableReferenceVerdict.Unreferenced;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Budget reservation {ReservationId}: citation sites could not be probed; the claim is kept", candidate.Id);

            return DurableReferenceVerdict.Indeterminate;
        }
    }

    public async Task<bool> SettleAsync(DurableRetentionCandidate candidate, DurableRetentionDecision decision, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(decision);

        return decision.Action == DurableRetentionAction.Collect
            ? await CollectAsync(candidate, cancellationToken).ConfigureAwait(false)
            : await StampAsync(candidate, decision.RetainUntil, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The delete, with every condition that admitted it repeated in its own statement: a claim another writer moved
    /// back into a live state, or that something began to cite, between the classification and now matches nothing and
    /// is simply left alone.
    /// </summary>
    private async Task<bool> CollectAsync(DurableRetentionCandidate candidate, CancellationToken cancellationToken)
    {
        await using var db = CreateDb();

        var deleted = await db.BudgetReservation
            .Where(claim => claim.Id == candidate.Id && claim.TeamId == candidate.TeamId && TerminalStates.Contains(claim.State)
                && !db.WorkflowRunModelCallAttempt.Any(attempt => attempt.BudgetReservationId == claim.Id)
                && !db.BudgetReservation.Any(child => child.ParentReservationId == claim.Id))
            .ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);

        if (deleted == 1)
            _logger.LogInformation("Budget reservation {ReservationId} reclaimed for team {TeamId}: terminal, outside every cap window, and cited by nothing", candidate.Id, candidate.TeamId);

        return deleted == 1;
    }

    /// <summary>The quarantine deadline, written only onto a row that is still terminal — the reservation has no revision of its own, so its identity and state are the fence.</summary>
    private async Task<bool> StampAsync(DurableRetentionCandidate candidate, DateTimeOffset? retainUntil, CancellationToken cancellationToken)
    {
        await using var db = CreateDb();

        var updated = await db.BudgetReservation
            .Where(claim => claim.Id == candidate.Id && claim.TeamId == candidate.TeamId && TerminalStates.Contains(claim.State))
            .ExecuteUpdateAsync(set => set.SetProperty(claim => claim.RetainUntil, retainUntil), cancellationToken).ConfigureAwait(false);

        return updated == 1;
    }

    private CodeSpaceDbContext CreateDb() => new(_dbOptions);
}
