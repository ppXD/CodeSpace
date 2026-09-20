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
/// <para><b>Which outcomes are settled.</b> Exactly the two <c>RunCleanupReceipt.IsSettled</c> already names:
/// <see cref="RunResourceOutcome.Completed"/> and <see cref="RunResourceOutcome.Compensated"/>. Neither of the other
/// two is, and each for its own reason. <see cref="RunResourceOutcome.Orphaned"/> is addressed to a sweep on the host
/// that owes the work and can still become Compensated; an orphan a retention pass removed is a resource nobody will
/// ever be told about again. <see cref="RunResourceOutcome.Unknown"/> is REWRITABLE — the ledger's upsert fences only
/// Completed and Compensated, and the orphan sweep answers Unknown when the host cannot even attempt a teardown, so a
/// live leak can move from Orphaned to Unknown and leave the orphan queue for good. It is also READ: the Room counts
/// Unknown receipts of the sweepable kinds and renders "N resources with an unknown cleanup state" with no age bound
/// at all, so draining them per-team-head would walk that count down to a card that quietly disappears — a wrong
/// number where the truth used to be.</para>
///
/// <para><b>Starvation.</b> A pinned receipt and an unsettled one are both excluded by the CLAIM query rather than
/// settled, so neither occupies a batch slot — which is why this plane needs one deadline column and not two. A keep
/// the cursor CAN reach — an unanswerable citation question — pushes the same deadline forward instead. Every claim
/// predicate is repeated in the deleting statement for the reason every fenced reaper repeats its guards: the claim
/// and the deletion are different transactions, and a pin, or a re-opened orphan, can land between them.</para>
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

    /// <summary>
    /// The outcomes a receipt is SETTLED in — the same two <c>RunCleanupReceipt.IsSettled</c> names, and the same two
    /// the ledger's upsert refuses to overwrite. Every absence is deliberate and pinned by a test.
    ///
    /// <para><see cref="SettledOutcomeNames"/> is the same set as the raw SQL sees it. The claim query cannot
    /// parameterise an <c>IN</c> list of enum names, so the two forms exist side by side — and a test compares them,
    /// because two copies of one rule drift silently.</para>
    /// </summary>
    internal static readonly RunResourceOutcome[] SettledOutcomes = [RunResourceOutcome.Completed, RunResourceOutcome.Compensated];

    /// <summary>The literal the claim and the delete use. Compared against <see cref="SettledOutcomes"/> by a test that reads this file's own SQL.</summary>
    internal const string SettledOutcomeNames = "'Completed', 'Compensated'";

    /// <summary>The pin kind the claim and the probe ask about, as the raw SQL spells it. Pinned against <see cref="DurablePinKind.CleanupReceipt"/> by the same test.</summary>
    internal const string PinnedKindName = "CleanupReceipt";

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
                WHERE receipt.outcome IN ('Completed', 'Compensated')
                  AND receipt.recorded_at <= {{window.TerminalBefore}}
                  AND (receipt.retain_until IS NULL OR receipt.retain_until <= {{window.Now}})
                  AND NOT EXISTS (SELECT 1 FROM paired_qualification_result_pin pin WHERE pin.kind = 'CleanupReceipt' AND pin.pinned_id = receipt.id)
                ORDER BY receipt.team_id, receipt.recorded_at, receipt.id
            )
            SELECT receipt.* FROM agent_run_cleanup_receipt receipt
            JOIN eligible ON eligible.id = receipt.id
            WHERE receipt.outcome IN ('Completed', 'Compensated')
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

    /// <summary>
    /// A keep this cursor cannot record any other way pushes the deadline FORWARD by the class's recheck interval —
    /// the row has no modification time of its own, so without that a receipt whose citation question could not be
    /// answered would be re-claimed on every tick for ever.
    ///
    /// <para>A CITATION is the exception, and honouring it is not optional: the decision clears the marker on
    /// purpose, because a quarantine that elapsed while something still pointed at the row never waited for anything.
    /// Writing a deadline there instead would let the row be collected on the FIRST uncited observation after the
    /// citation went away — the second wait skipped, which is the one thing this column exists to prevent. It costs
    /// no hot loop: a still-cited receipt is excluded by the claim query, not re-claimed.</para>
    /// </summary>
    public async Task<bool> SettleAsync(DurableRetentionSweepWindow window, DurableRetentionCandidate candidate, DurableRetentionDecision decision, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(decision);

        if (decision.Action == DurableRetentionAction.Collect) return await CollectAsync(window, candidate, cancellationToken).ConfigureAwait(false);

        var deadline = decision.Action switch
        {
            DurableRetentionAction.Quarantine => decision.RetainUntil,
            DurableRetentionAction.Referenced => null,
            _ => window.RecheckAt,
        };

        return await StampAsync(window, candidate, deadline, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The delete, with every condition that admitted it repeated in its own statement. The outcome guard is what
    /// keeps a receipt an orphan sweep re-opened between the claim and now — Orphaned again, addressed to a host —
    /// from being removed by a decision taken while it still read as settled.
    /// </summary>
    private async Task<bool> CollectAsync(DurableRetentionSweepWindow window, DurableRetentionCandidate candidate, CancellationToken cancellationToken)
    {
        await using var db = CreateDb();

        var deleted = await db.AgentRunCleanupReceipt
            .Where(receipt => receipt.Id == candidate.Id && receipt.TeamId == candidate.TeamId
                && SettledOutcomes.Contains(receipt.Outcome)
                && receipt.RecordedAt <= window.TerminalBefore
                && receipt.RetainUntil != null && receipt.RetainUntil <= window.Now
                && !db.PairedQualificationResultPin.Any(pin => pin.Kind == DurablePinKind.CleanupReceipt && pin.PinnedId == receipt.Id))
            .ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);

        if (deleted == 1)
            _logger.LogInformation("Cleanup receipt {ReceiptId} reclaimed for team {TeamId}: settled, past its rule, and cited by nothing", candidate.Id, candidate.TeamId);

        return deleted == 1;
    }

    /// <summary>The deadline, written only onto a row that is still the one the claim saw — the receipt has no revision, so its identity, its outcome and its own age are the fence.</summary>
    private async Task<bool> StampAsync(DurableRetentionSweepWindow window, DurableRetentionCandidate candidate, DateTimeOffset? retainUntil, CancellationToken cancellationToken)
    {
        await using var db = CreateDb();

        var updated = await db.AgentRunCleanupReceipt
            .Where(receipt => receipt.Id == candidate.Id && receipt.TeamId == candidate.TeamId
                && SettledOutcomes.Contains(receipt.Outcome) && receipt.RecordedAt <= window.TerminalBefore)
            .ExecuteUpdateAsync(set => set.SetProperty(receipt => receipt.RetainUntil, retainUntil), cancellationToken).ConfigureAwait(false);

        return updated == 1;
    }

    private CodeSpaceDbContext CreateDb() => new(_dbOptions);
}
