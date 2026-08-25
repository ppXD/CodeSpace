using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Messages.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace CodeSpace.Core.Services.RunData;

/// <summary>
/// The ONE way a facet's producer states something about the completeness of a run's record — the write side of
/// <c>workflow_run_data_manifest</c> and <c>workflow_run_capture_gap</c> (migration 0146), for every facet rather than
/// for one.
///
/// <para><b>Why this seam exists at all, which is a lock-ordering story.</b> 0146's guards take the per-run rendezvous
/// lock in a BEFORE ROW trigger — which fires only after an INSERT's value expressions have already been evaluated
/// against the statement snapshot. So a producer that probed the run's open gaps and THEN wrote had its whole statement
/// refused whenever a gap committed in between, and because both counts are deltas a refused statement is not a
/// retryable no-op: the delta is gone and the run's expectation is understated permanently. The first producer avoided
/// that by taking the lock explicitly before probing, which worked and was a rule someone had to remember.
/// Migration 0148 moved it inside the database: the lock, the probe and the write are one function whose FIRST
/// statement is the lock, so there is no order left to get wrong. This interface is how C# reaches those functions
/// without restating their SQL — a source-level pin in the unit suite keeps the SQL from reappearing here.</para>
///
/// <para><b>Every call is contained and runs on its OWN unit of work.</b> A claim about the record is always safe to
/// lose while a record is not, so no failure here may take a producer's records down with it or change what its run
/// resolves to. That is a property of the wiring rather than a promise: each method opens its own scope, so the
/// caller's DbContext and any transaction it is holding are untouched, and a refusal is logged and reported as
/// <c>false</c> rather than thrown.</para>
///
/// <para><b>Production reads are observation-only.</b> <see cref="IRunDataCompletenessReader"/> exposes bounded
/// manifest metadata to Workflow Run operators, while the Agent Run summary exposes bounded gaps carrying exact
/// process-attempt attribution. Neither read is an execution authority, and no terminal verdict, planner, oracle,
/// completion or routing path consumes either one. Wiring a terminal verdict before every facet has a producer would
/// park every run, since a facet with no statement is indeterminate.</para>
/// </summary>
public interface IRunDataCompletenessWriter
{
    /// <summary>Idempotently states zero for every registered producer facet before a run can emit records.</summary>
    Task<bool> InitializeAsync(RunDataManifestInitialization initialization, CancellationToken cancellationToken) => Task.FromResult(true);

    /// <summary>
    /// Fold one delta into the facet's statement, computing the verdict in the database so a producer never offers 0146
    /// a claim it would refuse. Returns whether the statement was made; a producer that ignores the answer is behaving
    /// correctly, because losing one claim may not change what its run resolves to.
    /// </summary>
    Task<bool> AdvanceAsync(RunDataFacetAdvance advance, CancellationToken cancellationToken);

    /// <summary>
    /// Record a span the producer KNOWS it missed. The gap is committed on its own, never together with a claim about
    /// the record: 0146's whole value system is that the bad news must survive whatever happens to the claim it
    /// contradicts, and a shared transaction would let a refused statement take the gap down with it. Its arrival
    /// downgrades every strictly readable statement of the run through 0146's own trigger, so the ORDER the two
    /// writers arrive in cannot decide the outcome.
    /// </summary>
    Task<bool> NoticeAsync(WorkflowRunCaptureGap gap, CancellationToken cancellationToken);

    /// <summary>
    /// The facet's expectation stops being knowable: <c>expected_record_count</c> becomes NULL, which 0146 refuses
    /// every complete verdict over. Returns whether a statement was actually revised — a facet with no statement gets
    /// no row invented for it, because an absent statement is already the indeterminate answer, and one already
    /// indeterminate is left alone rather than re-revised.
    /// </summary>
    Task<bool> UnstateExpectationAsync(Guid teamId, Guid workflowRunId, string facet, CancellationToken cancellationToken);
}

/// <inheritdoc cref="IRunDataCompletenessWriter"/>
public sealed class RunDataCompletenessWriter : IRunDataCompletenessWriter, IScopedDependency
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<RunDataCompletenessWriter> _logger;

    public RunDataCompletenessWriter(IServiceScopeFactory scopeFactory, ILogger<RunDataCompletenessWriter> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    public async Task<bool> InitializeAsync(RunDataManifestInitialization initialization, CancellationToken cancellationToken) =>
        await ContainedAsync(initialization.WorkflowRunId, WorkflowRunDataOwnerKinds.DataManifest, async db => await db.Database.ExecuteSqlAsync(
            $"SELECT workflow_run_data_manifest_initialize({initialization.TeamId}, {initialization.WorkflowRunId}, {RunDataManifestCoverage.RequiredFacets.ToArray()}, {WorkflowRunDataContract.CurrentVersion})",
            cancellationToken).ConfigureAwait(false), cancellationToken).ConfigureAwait(false);

    public async Task<bool> AdvanceAsync(RunDataFacetAdvance advance, CancellationToken cancellationToken) =>
        await ContainedAsync(advance.WorkflowRunId, advance.Facet, async db => await db.Database.ExecuteSqlAsync(
            $"SELECT workflow_run_data_manifest_advance({advance.TeamId}, {advance.WorkflowRunId}, {advance.Facet}, {advance.Expected}, {advance.Present}, {advance.Masked}, {WorkflowRunDataContract.CurrentVersion})",
            cancellationToken).ConfigureAwait(false), cancellationToken).ConfigureAwait(false);

    public async Task<bool> NoticeAsync(WorkflowRunCaptureGap gap, CancellationToken cancellationToken) =>
        await ContainedAsync(gap.WorkflowRunId, gap.SubjectKind, async db =>
        {
            db.WorkflowRunCaptureGap.Add(gap);
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);

    public async Task<bool> UnstateExpectationAsync(Guid teamId, Guid workflowRunId, string facet, CancellationToken cancellationToken)
    {
        var revised = 0L;

        await ContainedAsync(workflowRunId, facet, async db => revised = await Revised(db, teamId, workflowRunId, facet, cancellationToken).ConfigureAwait(false), cancellationToken).ConfigureAwait(false);

        return revised > 0;
    }

    /// <summary>How many statements the un-stating revised — read back so the caller can name the run it happened to and stay silent when nothing changed.</summary>
    private static async Task<long> Revised(CodeSpaceDbContext db, Guid teamId, Guid workflowRunId, string facet, CancellationToken cancellationToken) =>
        (await db.Database.SqlQuery<long>($"SELECT workflow_run_data_manifest_unstate_expectation({teamId}, {workflowRunId}, {facet}) AS \"Value\"")
            .ToListAsync(cancellationToken).ConfigureAwait(false)).Single();

    /// <summary>
    /// One write, on this writer's OWN scope, contained. The scope is what keeps a refused claim off the caller's
    /// DbContext, and the containment is what keeps it out of the caller's outcome — with one exception that is not
    /// containment at all: a cancellation while cancellation was requested is the round ending, and re-reporting it as
    /// a lost claim would log noise for every torn-down worker.
    /// </summary>
    private async Task<bool> ContainedAsync(Guid workflowRunId, string facet, Func<CodeSpaceDbContext, Task> write, CancellationToken cancellationToken)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();

            await write(scope.ServiceProvider.GetRequiredService<CodeSpaceDbContext>()).ConfigureAwait(false);

            return true;
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(exception, "The {Facet} facet of workflow run {WorkflowRunId} could not state its completeness; the statement is lost, the records it describes are untouched, and the run resolves exactly as it does with no statement at all", facet, workflowRunId);

            return false;
        }
    }
}
