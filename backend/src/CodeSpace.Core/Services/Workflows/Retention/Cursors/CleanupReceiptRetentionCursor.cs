using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Messages.Agents.Recovery;
using CodeSpace.Messages.Retention;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CodeSpace.Core.Services.Workflows.Retention.Cursors;

/// <summary>
/// Reclaims a settled <c>agent_run_cleanup_receipt</c> row that nothing cites any more.
///
/// <para><b>A receipt is a ledger row, not bytes, so reclaiming it means deleting it.</b> There is nothing to purge
/// first and no tombstone to leave: the receipt's whole content is "what became of this resource", and a row that is
/// gone reads as a run that left nothing behind — which is exactly what a settled receipt already says. Deletion is
/// admitted: <c>agent_run_cleanup_receipt</c> carries no trigger, and its only foreign key points AT the run, so a
/// receipt can go while its run stays.</para>
///
/// <para><b>Which outcomes are terminal, and the one that is not.</b> <see cref="RunResourceOutcome.Completed"/> and
/// <see cref="RunResourceOutcome.Compensated"/> are settled by definition. <see cref="RunResourceOutcome.Unknown"/>
/// joins them because nothing will ever settle it: the only sweep that revisits a receipt selects
/// <c>outcome = 'Orphaned'</c> (migration 0229's partial index is the whole of its queue), so an Unknown row is
/// terminal in practice however it reads — and a diagnosis nobody read in thirty days is not made more legible by
/// keeping it for ever. <see cref="RunResourceOutcome.Orphaned"/> is NEVER reclaimed: it is addressed to a sweep, it
/// names the host that owes the work, and it can still become Compensated. An orphan removed by a retention pass is a
/// resource nobody will ever be told about again.</para>
///
/// <para><b>Starvation.</b> A pinned receipt is excluded by the CLAIM query rather than settled, so it never occupies
/// a batch slot — which is why this plane needs one deadline column and not two. The predicate is repeated in the
/// classification for the reason every fenced reaper repeats its guards: the claim and the deletion are different
/// transactions, and a pin can land between them.</para>
/// </summary>
public sealed class CleanupReceiptRetentionCursor : IDurableRetentionCursor, IScopedDependency
{
    /// <summary>
    /// Every place a settled cleanup receipt can still be cited. One entry, and the list exists so that a SECOND one
    /// is a deliberate edit: a citer missing from it makes the cursor delete a row something still points at, which is
    /// the one failure mode of this class. Public and pinned by a test, like the log-stream cursor's.
    ///
    /// <para>Deliberately NOT citers, each for a stated reason. <c>RoomProjector.AgentRecoveryAsync</c> reads every
    /// receipt of a turn's agents, but by RUN and as an aggregate — a row that is gone folds to "nothing unsettled",
    /// which is what a settled receipt already meant. <c>AgentRunOrphanReaper</c> reads only Orphaned rows, which this
    /// cursor never claims.</para>
    /// </summary>
    public static readonly IReadOnlyList<(string Table, string Column)> CitationSites =
    [
        ("paired_qualification_result_pin", "pinned_id"),
    ];

    /// <summary>The outcomes nothing will settle again. <see cref="RunResourceOutcome.Orphaned"/> is absent on purpose and its absence is pinned by a test.</summary>
    internal static readonly RunResourceOutcome[] SettledOutcomes =
        [RunResourceOutcome.Completed, RunResourceOutcome.Compensated, RunResourceOutcome.Unknown];

    private readonly DbContextOptions<CodeSpaceDbContext> _dbOptions;
    private readonly ILogger<CleanupReceiptRetentionCursor> _logger;

    public CleanupReceiptRetentionCursor(DbContextOptions<CodeSpaceDbContext> dbOptions, ILogger<CleanupReceiptRetentionCursor> logger)
    {
        _dbOptions = dbOptions;
        _logger = logger;
    }

    public DurableRecordClass Class => DurableRecordClass.CleanupReceipt;

    /// <summary>
    /// The per-team head of the eligible queue, oldest first. <c>DISTINCT ON (team_id)</c> is materialized first so one
    /// tenant with a long backlog cannot starve the others, and every guard is repeated on the outer select because
    /// quals inside a materialized CTE are not re-evaluated against a row another worker changed in between.
    /// </summary>
    public async Task<IReadOnlyList<DurableRetentionCandidate>> ClaimAsync(DurableRetentionSweepWindow window, int limit, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(window);
        await using var db = CreateDb();

        var rows = await db.AgentRunCleanupReceipt.FromSqlInterpolated($$"""
            WITH eligible AS MATERIALIZED (
                SELECT DISTINCT ON (receipt.team_id) receipt.id, receipt.recorded_at
                FROM agent_run_cleanup_receipt receipt
                WHERE receipt.outcome IN ('Completed', 'Compensated', 'Unknown')
                  AND receipt.recorded_at <= {{window.TerminalBefore}}
                  AND (receipt.retain_until IS NULL OR receipt.retain_until <= {{window.Now}})
                  AND NOT EXISTS (SELECT 1 FROM paired_qualification_result_pin pin WHERE pin.kind = 'CleanupReceipt' AND pin.pinned_id = receipt.id)
                ORDER BY receipt.team_id, receipt.recorded_at, receipt.id
            )
            SELECT receipt.* FROM agent_run_cleanup_receipt receipt
            JOIN eligible ON eligible.id = receipt.id
            WHERE receipt.outcome IN ('Completed', 'Compensated', 'Unknown')
              AND receipt.recorded_at <= {{window.TerminalBefore}}
              AND (receipt.retain_until IS NULL OR receipt.retain_until <= {{window.Now}})
            ORDER BY receipt.recorded_at, receipt.id
            LIMIT {{limit}}
            """).AsNoTracking().ToListAsync(cancellationToken).ConfigureAwait(false);

        return rows.Select(receipt => new DurableRetentionCandidate(receipt.Id, receipt.TeamId, 0, receipt.RecordedAt, receipt.RetainUntil)).ToArray();
    }

    /// <summary>Fail-closed: any failure to probe the citation site answers indeterminate, which every consumer reads as keep.</summary>
    public async Task<DurableReferenceVerdict> ClassifyAsync(DurableRetentionCandidate candidate, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(candidate);

        try
        {
            await using var db = CreateDb();
            var pinned = await db.PairedQualificationResultPin.AsNoTracking()
                .AnyAsync(pin => pin.Kind == DurablePinKind.CleanupReceipt && pin.PinnedId == candidate.Id, cancellationToken).ConfigureAwait(false);

            return pinned ? DurableReferenceVerdict.Referenced : DurableReferenceVerdict.Unreferenced;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Cleanup receipt {ReceiptId}: citation sites could not be probed; the receipt is kept", candidate.Id);

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
    /// The delete, with every condition that admitted it repeated in its own statement. The outcome guard is what
    /// keeps a receipt an orphan sweep re-opened between the claim and now — Orphaned again, addressed to a host —
    /// from being removed by a decision taken while it still read as settled.
    /// </summary>
    private async Task<bool> CollectAsync(DurableRetentionCandidate candidate, CancellationToken cancellationToken)
    {
        await using var db = CreateDb();

        var deleted = await db.AgentRunCleanupReceipt
            .Where(receipt => receipt.Id == candidate.Id && receipt.TeamId == candidate.TeamId
                && SettledOutcomes.Contains(receipt.Outcome)
                && !db.PairedQualificationResultPin.Any(pin => pin.Kind == DurablePinKind.CleanupReceipt && pin.PinnedId == receipt.Id))
            .ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);

        if (deleted == 1)
            _logger.LogInformation("Cleanup receipt {ReceiptId} reclaimed for team {TeamId}: settled, past its rule, and cited by nothing", candidate.Id, candidate.TeamId);

        return deleted == 1;
    }

    /// <summary>The quarantine deadline, written only onto a row that is still the one the claim saw — the receipt has no revision, so its own identity and outcome are the fence.</summary>
    private async Task<bool> StampAsync(DurableRetentionCandidate candidate, DateTimeOffset? retainUntil, CancellationToken cancellationToken)
    {
        await using var db = CreateDb();

        var updated = await db.AgentRunCleanupReceipt
            .Where(receipt => receipt.Id == candidate.Id && receipt.TeamId == candidate.TeamId && SettledOutcomes.Contains(receipt.Outcome))
            .ExecuteUpdateAsync(set => set.SetProperty(receipt => receipt.RetainUntil, retainUntil), cancellationToken).ConfigureAwait(false);

        return updated == 1;
    }

    private CodeSpaceDbContext CreateDb() => new(_dbOptions);
}
