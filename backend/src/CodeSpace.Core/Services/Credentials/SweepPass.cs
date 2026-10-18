using Microsoft.Extensions.Logging;

namespace CodeSpace.Core.Services.Credentials;

/// <summary>A row a sweep may act on: ordered by this id, so a pass can walk the backlog with a cursor.</summary>
internal interface ISweepCandidate
{
    Guid Id { get; }
}

/// <summary>The next page of candidates whose id is greater than <paramref name="after"/> (from the start when null), in id order, at most <paramref name="take"/> of them.</summary>
internal delegate Task<List<TCandidate>> LoadSweepPage<TCandidate>(Guid? after, int take, CancellationToken cancellationToken);

/// <summary>Act on one candidate on its own save. True when it did what the sweep is for; false when there turned out to be nothing to do. A throw is a failure.</summary>
internal delegate Task<bool> AttemptSweepCandidate<TCandidate>(TCandidate candidate, CancellationToken cancellationToken);

/// <summary>The bounds of a <see cref="SweepPass{TCandidate}"/>, per slot of its batch.</summary>
internal static class SweepPassBounds
{
    /// <summary>Candidates a pass may attempt per slot of its batch: a candidate that fails is attempted, and uses up one of these, but not a slot of the batch.</summary>
    public const int AttemptsPerBatchSlot = 4;

    /// <summary>Candidates a pass may look at per slot of its batch, set-aside ones included — generous, because it is what lets a pass walk past them.</summary>
    public const int ExaminedPerBatchSlot = 100;
}

/// <summary>
/// One pass of a recurring sweep over a backlog that is not allowed to starve: the departed-member retirement and the
/// stranded-binding repair both read their candidates in id order and keep a candidate that fails, which is right — the
/// next tick may succeed — and which, with every pass reading from the front, let a batch's worth of candidates that can
/// never succeed hold every place in it for good.
///
/// <para>So a pass walks the backlog in pages behind a cursor: a failure is logged and skipped, costs no place in the batch
/// and is not looked at again by this pass. It stops when <c>batchSize</c> candidates are done or the backlog ends — and is
/// bounded in work as well as in results: it attempts at most <see cref="SweepPassBounds.AttemptsPerBatchSlot"/> candidates per slot of the
/// batch, and examines at most <see cref="SweepPassBounds.ExaminedPerBatchSlot"/>, which is also what ends a walk whose loader stopped
/// honouring its cursor. A candidate that has failed several passes in a row is set aside by
/// <see cref="ISweepFailureBackoff"/> and neither attempted nor logged until its window ends.</para>
/// </summary>
internal sealed class SweepPass<TCandidate> where TCandidate : ISweepCandidate
{
    private readonly string _name;
    private readonly ISweepFailureBackoff _backoff;
    private readonly ILogger _logger;

    public SweepPass(string name, ISweepFailureBackoff backoff, ILogger logger)
    {
        _name = name;
        _backoff = backoff;
        _logger = logger;
    }

    public async Task<int> RunAsync(int batchSize, LoadSweepPage<TCandidate> loadPage, AttemptSweepCandidate<TCandidate> attempt, CancellationToken cancellationToken)
    {
        var tally = new Tally(batchSize);
        Guid? after = null;

        while (tally.HasRoom)
        {
            var page = await loadPage(after, batchSize, cancellationToken).ConfigureAwait(false);

            if (page.Count == 0 || DidNotAdvance(page, after)) break;

            await WorkPageAsync(page, attempt, tally, cancellationToken).ConfigureAwait(false);

            if (page.Count < batchSize) break;

            after = page[^1].Id;
        }

        return tally.Done;
    }

    /// <summary>
    /// A page that ends where the previous one did means the loader ignored its cursor; reading on would read the same
    /// page for ever. Compared for equality, not order: a <see cref="Guid"/> does not sort in .NET as a Postgres uuid does,
    /// and the examined cap is what ends a loader that fails in some way this does not name.
    /// </summary>
    private bool DidNotAdvance(List<TCandidate> page, Guid? after)
    {
        if (after is not Guid cursor || page[^1].Id != cursor) return false;

        _logger.LogError("{Sweep}: the page after {Cursor} ended at the same candidate; the pass stops rather than read the same candidates again", _name, cursor);

        return true;
    }

    private async Task WorkPageAsync(List<TCandidate> page, AttemptSweepCandidate<TCandidate> attempt, Tally tally, CancellationToken cancellationToken)
    {
        foreach (var candidate in page)
        {
            if (!tally.HasRoom) return;

            tally.Examined++;

            if (_backoff.IsBackedOff(_name, candidate.Id)) continue;

            tally.Attempts++;

            if (await TryAsync(candidate, attempt, cancellationToken).ConfigureAwait(false)) tally.Done++;
        }
    }

    private async Task<bool> TryAsync(TCandidate candidate, AttemptSweepCandidate<TCandidate> attempt, CancellationToken cancellationToken)
    {
        try
        {
            var done = await attempt(candidate, cancellationToken).ConfigureAwait(false);

            _backoff.RecordSuccess(_name, candidate.Id);

            return done;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogFailure(ex, candidate.Id, _backoff.RecordFailure(_name, candidate.Id));

            return false;
        }
    }

    private void LogFailure(Exception ex, Guid candidateId, bool setAside)
    {
        if (setAside)
            _logger.LogWarning(ex, "{Sweep}: candidate {CandidateId} failed again and is set aside — it is not attempted again until its back-off window ends", _name, candidateId);
        else
            _logger.LogWarning(ex, "{Sweep}: candidate {CandidateId} failed; the pass continues — it stays a candidate", _name, candidateId);
    }

    private sealed class Tally
    {
        private readonly int _batchSize;

        public Tally(int batchSize) { _batchSize = batchSize; }

        public int Done { get; set; }
        public int Attempts { get; set; }
        public int Examined { get; set; }

        public bool HasRoom => Done < _batchSize && Attempts < (long)_batchSize * SweepPassBounds.AttemptsPerBatchSlot && Examined < (long)_batchSize * SweepPassBounds.ExaminedPerBatchSlot;
    }
}
