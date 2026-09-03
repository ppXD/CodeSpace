using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Messages.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CodeSpace.Core.Services.Agents.Eval;

/// <summary>
/// The bounded catch-up pass: terminal contract-era runs with NO <c>run_scorecard</c> row, projected through the
/// SAME <see cref="IRunScorecardWriter"/> the shadow sweep uses. One indexed NOT-EXISTS predicate makes the sweep
/// self-terminating — a run leaves the candidate set the moment its row lands, so it can never starve behind a
/// busier neighbour and the backlog only shrinks.
///
/// <para>Per-run try/catch, like the shadow sweep it shadows: one run's projection fault leaves that run a
/// candidate for the next tick and never aborts the pass.</para>
/// </summary>
public sealed class RunScorecardBackfillService : IRunScorecardBackfillService, IScopedDependency
{
    private readonly CodeSpaceDbContext _db;
    private readonly IRunScorecardWriter _writer;
    private readonly ILogger<RunScorecardBackfillService> _logger;

    public RunScorecardBackfillService(CodeSpaceDbContext db, IRunScorecardWriter writer, ILogger<RunScorecardBackfillService> logger)
    {
        _db = db;
        _writer = writer;
        _logger = logger;
    }

    public async Task<int> BackfillAsync(int batchSize, CancellationToken cancellationToken)
    {
        var candidates = await _db.WorkflowRun.AsNoTracking()
            .Where(r => r.CompletionPolicyVersion != null
                        && (r.Status == WorkflowRunStatus.Success || r.Status == WorkflowRunStatus.Failure || r.Status == WorkflowRunStatus.Cancelled)
                        && !_db.RunScorecard.Any(s => s.WorkflowRunId == r.Id))
            .OrderByDescending(r => r.CreatedDate)
            .Take(batchSize)
            .Select(r => new { r.Id, r.TeamId })
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        var written = 0;

        foreach (var run in candidates)
        {
            try
            {
                if (await _writer.WriteAsync(run.Id, run.TeamId, cancellationToken).ConfigureAwait(false)) written++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Run scorecard backfill failed for run {RunId}; the pass continues — the run stays a candidate", run.Id);
            }
        }

        return written;
    }
}
