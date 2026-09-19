using CodeSpace.Core.Persistence.Db;
using CodeSpace.Messages.Retention;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CodeSpace.Core.Services.Workflows.Retention;

/// <summary>
/// The generic claim / decide / act loop, shared by every retention plane that is not the artifact ledger.
///
/// <para><b>What is structural rather than checked.</b> (1) The cutoff a cursor claims against is derived HERE from
/// the class's own age floor, so a cursor cannot widen its candidate set past the policy. (2) The decision is
/// <see cref="DurableRetentionDecision.Decide"/> and nothing else, so every plane inherits the same two waits.
/// (3) Only <see cref="DurableRetentionAction.Collect"/> removes anything, and it is reachable only after both waits
/// elapsed with a definite "nothing cites this". (4) A candidate that throws is logged and KEPT — the sweep never
/// converts an unanswered question into a deletion.</para>
///
/// <para><b>Why there is no lease.</b> The artifact reaper owns a ledger row and fences it with an owner and an epoch.
/// These planes have no ledger, and inventing one would mean a column per plane. The fence instead comes from the two
/// writes a collection actually makes: the byte removal goes through the CAS location's own durable claim, and the
/// tombstone is a conditional update on the record's revision. Two workers sweeping the same record at the same time
/// therefore remove the bytes at most once, and exactly one of them writes the tombstone; the loser settles nothing
/// and the next sweep meets a row it never held.</para>
///
/// <para><b>Why every decision is settled, including a keep.</b> A record nothing can ever reclaim — one a sealed
/// result pins for good, one whose bytes another record also names — is claimed by the oldest-first query on every
/// tick. If a keep wrote nothing, a few hundred such records deployment-wide would fill the batch for ever and the
/// sweep would report a healthy-looking claim count while collecting nothing, never reaching the records behind
/// them. So a keep is a settlement too: it advances the record's own modification time, and the claim query leaves
/// it alone for the class's recheck interval.</para>
/// </summary>
public sealed class DurableRetentionReaper : IDurableRetentionReaper
{
    /// <summary>Records claimed per cursor per sweep. The cadence is hourly and every rule is measured in days, so the ceiling changes only how promptly a backlog drains — never what is collected.</summary>
    private const int BatchSize = 200;

    /// <summary>Records asked for per claim. A fair cursor returns the per-tenant head, so the batch above is reached by asking repeatedly rather than in one query, and a tenant with a long backlog never holds the whole batch.</summary>
    private const int ClaimSize = 25;

    private static readonly TimeSpan CandidateTimeout = TimeSpan.FromMinutes(2);

    private readonly DbContextOptions<CodeSpaceDbContext> _dbOptions;
    private readonly IReadOnlyList<IDurableRetentionCursor> _cursors;
    private readonly ILogger<DurableRetentionReaper> _logger;

    public DurableRetentionReaper(DbContextOptions<CodeSpaceDbContext> dbOptions, IEnumerable<IDurableRetentionCursor> cursors, ILogger<DurableRetentionReaper> logger)
    {
        _dbOptions = dbOptions;
        _cursors = cursors.OrderBy(cursor => cursor.Class).ToArray();
        _logger = logger;
    }

    public async Task<DurableRetentionSweepSummary> SweepAsync(CancellationToken cancellationToken)
    {
        var now = await DatabaseClockAsync(cancellationToken).ConfigureAwait(false);
        var counts = new SweepCounts();

        foreach (var cursor in _cursors)
            await SweepCursorAsync(cursor, now, counts, cancellationToken).ConfigureAwait(false);

        return counts.Summary();
    }

    /// <summary>One plane's bounded pass. A class the running policy does not register claims nothing at all, which is the same "keep forever" the decision returns for it.</summary>
    private async Task SweepCursorAsync(IDurableRetentionCursor cursor, DateTimeOffset now, SweepCounts counts, CancellationToken cancellationToken)
    {
        var rule = DurableRetentionPolicy.For(cursor.Class);

        if (rule is null)
        {
            _logger.LogWarning("Durable retention: no rule is registered for {RecordClass}, so its records are kept and never claimed", cursor.Class);
            return;
        }

        var window = new DurableRetentionSweepWindow(now, now.Subtract(rule.MinimumAge), now.Subtract(rule.RecheckInterval));
        var seen = new HashSet<Guid>();

        while (counts.Claimed < BatchSize)
        {
            var limit = Math.Min(ClaimSize, BatchSize - counts.Claimed);
            var claimed = await cursor.ClaimAsync(window, limit, cancellationToken).ConfigureAwait(false);
            // A cursor that claims fairly hands back one record per tenant, so the batch is reached by asking again —
            // and the ids already settled this tick are excluded, because a settlement that writes nothing (a drain
            // still making progress) leaves the record eligible and the loop would otherwise spin on it.
            var candidates = claimed.Where(candidate => seen.Add(candidate.Id)).ToList();

            if (candidates.Count == 0) break;

            counts.Claimed += candidates.Count;

            foreach (var candidate in candidates)
                counts.Record(await SweepCandidateAsync(cursor, rule, candidate, now, cancellationToken).ConfigureAwait(false));
        }
    }

    /// <summary>One candidate, start to finish. Every exit that is not a completed settlement keeps the record.</summary>
    private async Task<DurableRetentionAction> SweepCandidateAsync(IDurableRetentionCursor cursor, DurableRetentionRule rule, DurableRetentionCandidate candidate,
        DateTimeOffset now, CancellationToken cancellationToken)
    {
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        operation.CancelAfter(CandidateTimeout);

        try
        {
            var verdict = await cursor.ClassifyAsync(candidate, operation.Token).ConfigureAwait(false);
            var decision = DurableRetentionDecision.Decide(rule, new DurableRetentionObservation(candidate.TerminalAt, candidate.RetainUntil, verdict, now));

            return await ApplyAsync(cursor, candidate, decision, operation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Durable retention: {RecordClass} {RecordId} could not be evaluated; the record is kept", cursor.Class, candidate.Id);

            return DurableRetentionAction.Indeterminate;
        }
    }

    /// <summary>
    /// Every decision is handed to the cursor, including the keeps: a keep that wrote nothing would be re-claimed on
    /// every tick for ever. A settlement the cursor could not complete — a lost race, a refused removal, a drain that
    /// needs another sweep — is reported as the keep it is.
    /// </summary>
    private static async Task<DurableRetentionAction> ApplyAsync(IDurableRetentionCursor cursor, DurableRetentionCandidate candidate,
        DurableRetentionDecision decision, CancellationToken cancellationToken) =>
        await cursor.SettleAsync(candidate, decision, cancellationToken).ConfigureAwait(false)
            ? decision.Action
            : DurableRetentionAction.Indeterminate;

    /// <summary>The database's clock, not the worker's: every deadline these decisions compare against was written by a database clock too.</summary>
    private async Task<DateTimeOffset> DatabaseClockAsync(CancellationToken cancellationToken)
    {
        await using var db = new CodeSpaceDbContext(_dbOptions);

        return await db.Database.SqlQueryRaw<DateTimeOffset>("SELECT clock_timestamp() AS \"Value\"").SingleAsync(cancellationToken).ConfigureAwait(false);
    }

    private sealed class SweepCounts
    {
        public int Claimed { get; set; }
        private int Quarantined { get; set; }
        private int Collected { get; set; }
        private int Referenced { get; set; }
        private int Kept { get; set; }

        public void Record(DurableRetentionAction action)
        {
            if (action == DurableRetentionAction.Quarantine) Quarantined++;
            else if (action == DurableRetentionAction.Collect) Collected++;
            else if (action == DurableRetentionAction.Referenced) Referenced++;
            else Kept++;
        }

        public DurableRetentionSweepSummary Summary() => new()
        {
            Claimed = Claimed, Quarantined = Quarantined, Collected = Collected, Referenced = Referenced, Kept = Kept,
        };
    }
}
