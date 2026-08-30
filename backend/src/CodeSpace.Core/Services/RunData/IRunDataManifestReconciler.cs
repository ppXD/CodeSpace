using System.Linq.Expressions;
using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Messages.Contracts;
using CodeSpace.Messages.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CodeSpace.Core.Services.RunData;

/// <summary>
/// The loop the manifest plane was alone in lacking.
///
/// <para>A producer declares what it undertakes to capture BEFORE the records land and accounts for them after — that
/// separation is the whole fail-closed direction of the plane, because it is what leaves <c>present</c> below
/// <c>expected</c> when the accounting is lost. What nothing did until now was come back for the runs where it WAS
/// lost. A worker killed between the declaration and the payload write leaves a terminal run whose facet states a
/// shortfall against an expectation nobody will ever meet, and whose gap plane says nothing about it, because the
/// producer that would have noticed the loss is the process that died.</para>
///
/// <para><b>It un-states; it never counts.</b> The only write it makes is
/// <see cref="IRunDataAbandonedExpectationWriter.UnstateAbandonedExpectationAsync"/> — the expectation becomes
/// indeterminate and the verdict lands on an honest not-complete arm. Advancing <c>present</c> to meet
/// <c>expected</c> would close the same shortfall and would make the run read as a complete record over data nobody
/// counted, which is the single false claim this whole plane exists to refuse.</para>
///
/// <para><b>And it never writes over an answer that got better.</b> The row is selected in one transaction and written
/// in another, so between the two a producer can commit its accounting, a gap can name the loss, or an operator can
/// continue the terminal run. The write re-asks the whole selecting question, which is why it goes through the
/// conditional seam and not the producer's unconditional one.</para>
/// </summary>
public interface IRunDataManifestReconciler : IScopedDependency
{
    /// <summary>Picks up a bounded batch of terminal runs' unattributed shortfalls and un-states each one's expectation.</summary>
    Task<RunDataManifestReconciliation> ReconcileUnattributedShortfallsAsync(int batchSize, CancellationToken cancellationToken);
}

/// <inheritdoc cref="IRunDataManifestReconciler"/>
public sealed class RunDataManifestReconciler : IRunDataManifestReconciler
{
    /// <summary>
    /// How long a facet must have gone unadvanced before its shortfall counts as abandoned rather than in flight. A
    /// producer states completeness on its own contained unit of work, off the run's transaction, so an accounting can
    /// commit after the run terminalizes — and this un-stating is permanent, so reaching a facet early costs a complete
    /// record that was about to be established. Held at or above the job's cadence so a facet always gets one whole
    /// tick of silence first.
    /// </summary>
    public static readonly TimeSpan SettlingWindow = TimeSpan.FromMinutes(15);

    private readonly CodeSpaceDbContext _db;
    private readonly IRunDataAbandonedExpectationWriter _writer;
    private readonly TimeProvider _clock;
    private readonly ILogger<RunDataManifestReconciler> _logger;

    public RunDataManifestReconciler(CodeSpaceDbContext db, IRunDataAbandonedExpectationWriter writer, TimeProvider clock, ILogger<RunDataManifestReconciler> logger)
    {
        _db = db;
        _writer = writer;
        _clock = clock;
        _logger = logger;
    }

    /// <summary>
    /// A shortfall nobody attributed to anything. Each conjunct excludes a statement that has a BETTER answer than
    /// "unknown" already: a determinate expectation is the only kind there is anything to un-state about, a met or
    /// exceeded one is not short, a known-missing span means the gap plane can already say WHAT is missing, and a
    /// statement still inside the settling window may yet be accounted for by a producer that is merely slow.
    ///
    /// <para>The verdict conjunct selects NOTHING the counts have not already selected —
    /// <c>ck_workflow_run_data_manifest_completeness</c> refuses both complete verdicts over a determinate expectation
    /// the present count falls short of, so it is true of every row the line above matches. It is here for the plan:
    /// it is the whole of what lets this query be served by the partial index over incomplete statements, and without
    /// it the sweep reads every facet of every run in the deployment every quarter hour.</para>
    /// </summary>
    public static Expression<Func<WorkflowRunDataManifest, bool>> UnattributedShortfall(DateTimeOffset settledBefore) => statement =>
        statement.ExpectedRecordCount != null && statement.PresentRecordCount < statement.ExpectedRecordCount
        && statement.KnownMissingCount == 0 && statement.LastModifiedAt <= settledBefore
        && statement.Verdict != WorkflowRunCaptureCompleteness.Exact && statement.Verdict != WorkflowRunCaptureCompleteness.RedactedExact;

    public async Task<RunDataManifestReconciliation> ReconcileUnattributedShortfallsAsync(int batchSize, CancellationToken cancellationToken)
    {
        var abandoned = await AbandonedQuery(_db, _clock.GetUtcNow() - SettlingWindow, batchSize).ToListAsync(cancellationToken).ConfigureAwait(false);

        return await UnstateEachAsync(abandoned, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The pass's own accounting over the batch it was handed: one conditional un-stating per candidate, and three
    /// counts in which every candidate lands in exactly one arm. A refusal is the ordinary answer for a row whose
    /// answer improved between the selecting read and the write, so it belongs in <c>Unchanged</c> and never in
    /// <c>Unstated</c>.
    ///
    /// <para>Internal rather than private so the arithmetic is unit-pinned directly against a chosen set
    /// (InternalsVisibleTo). The read above is deployment-wide over a shared database, so a real pass's counts are made
    /// of whatever unattributed shortfalls the whole deployment happens to be carrying — a number no test can assert
    /// without asserting a stranger's backlog.</para>
    /// </summary>
    internal async Task<RunDataManifestReconciliation> UnstateEachAsync(IReadOnlyList<RunDataAbandonedExpectation> abandoned, CancellationToken cancellationToken)
    {
        var unstated = 0;

        foreach (var candidate in abandoned)
        {
            var revised = await _writer.UnstateAbandonedExpectationAsync(candidate, cancellationToken).ConfigureAwait(false);

            if (revised) unstated++;
        }

        if (unstated > 0)
            _logger.LogInformation("Un-stated {Unstated} of {Examined} facets whose terminal run declared more records than any producer accounted for and left no gap naming the loss", unstated, abandoned.Count);

        return new RunDataManifestReconciliation { Examined = abandoned.Count, Unstated = unstated, Unchanged = abandoned.Count - unstated };
    }

    /// <summary>
    /// The batch. It is served by <c>ix_workflow_run_data_manifest_incomplete</c> — the partial index over exactly the
    /// statements that are not complete — because the predicate carries that index's own condition and orders by its
    /// columns, so a sweep with nothing to do walks a few index pages instead of sequentially scanning every facet of
    /// every run in the deployment. Oldest first within a team, so a backlog drains in the order it accumulated; each
    /// statement leaves the candidate set for good once un-stated, so the sweep converges rather than revisiting.
    ///
    /// <para>Exposed as a query rather than a list so a test can read the SQL this actually emits and EXPLAIN it: the
    /// index is reachable only while the predicate keeps the shape the planner can prove implies the index's own, and
    /// nothing about losing it would be visible in a result.</para>
    /// </summary>
    public static IQueryable<RunDataAbandonedExpectation> AbandonedQuery(CodeSpaceDbContext db, DateTimeOffset settledBefore, int batchSize) =>
        db.WorkflowRunDataManifest.AsNoTracking()
            .Where(UnattributedShortfall(settledBefore))
            .Where(statement => db.WorkflowRun.AsNoTracking().Where(IsTerminal).Select(run => run.Id).Contains(statement.WorkflowRunId))
            .OrderBy(statement => statement.TeamId).ThenBy(statement => statement.LastModifiedAt).ThenBy(statement => statement.Id)
            .Take(batchSize)
            .Select(statement => new RunDataAbandonedExpectation
            {
                TeamId = statement.TeamId, WorkflowRunId = statement.WorkflowRunId, Facet = statement.Facet, SettledBefore = settledBefore,
            });

    /// <summary>A run nothing will advance again. Spelled as the reader spells it, because a facet of a Suspended run is mid-flight and not short of anything.</summary>
    private static readonly Expression<Func<WorkflowRun, bool>> IsTerminal = run =>
        run.Status == WorkflowRunStatus.Success || run.Status == WorkflowRunStatus.Failure || run.Status == WorkflowRunStatus.Cancelled;
}
