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
/// manifest metadata to Workflow Run operators, while the Agent Run summary exposes the bounded gaps that NAME one
/// Agent Run, reporting each one's process attribution where it has one. Neither read is an execution authority, and
/// no terminal verdict, planner, oracle, completion or routing path consumes either one. Wiring a terminal verdict
/// before every facet has a producer would park every run, since a facet with no statement is indeterminate.</para>
/// </summary>
public interface IRunDataCompletenessWriter
{
    /// <summary>
    /// Idempotently states that every registered producer facet EXISTS and that nobody has established what it should
    /// contain — an expectation of NULL, which 0146 refuses every complete verdict over. It declares a plane, never a
    /// count: a zero here would be the determinate claim "this facet is expected to be empty", and a run that died
    /// before its producers counted anything would read back as a complete record.
    ///
    /// <para>It carries no default implementation ON PURPOSE. It shipped with one — a body returning <c>true</c> —
    /// which no test double overrode, so every completeness-flow test ran against a run whose manifest this method had
    /// never touched, and the state it actually creates was unreachable from the suite that was supposed to cover it.
    /// A seam whose no-op is invisible to every double is not covered by them.</para>
    /// </summary>
    Task<bool> InitializeAsync(RunDataManifestInitialization initialization, CancellationToken cancellationToken);

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

/// <summary>
/// The un-stating a caller that did not OBSERVE the abandonment is allowed to make. A sibling of
/// <see cref="IRunDataCompletenessWriter"/> rather than a fifth method on it (Rule 7), because the two verbs answer to
/// different authorities: a producer states abandonment as a fact about ITSELF and nothing may talk it out of that,
/// while a sweep only ever infers it from a row it read in an earlier transaction, and an inference has to prove it
/// still holds. Widening the producer seam would have handed every producer a verb whose extra argument means nothing
/// to it, and handed the sweep the unconditional one it must never reach for.
/// </summary>
public interface IRunDataAbandonedExpectationWriter
{
    /// <summary>
    /// Un-states one facet's expectation ONLY IF it still reads exactly as the caller selected it: an unattributed
    /// shortfall, on a run still terminal, unadvanced since <see cref="RunDataAbandonedExpectation.SettledBefore"/>.
    /// Every conjunct is re-checked inside the write, so a producer's late accounting, a gap that names the loss, or
    /// an operator continuing the run between the read and this call keeps what it established.
    ///
    /// <para>Returns whether a statement was actually revised. <c>false</c> is the ordinary answer for a row that
    /// stopped qualifying, and it is not a failure to retry — the answer improved, so there is nothing left to
    /// un-state.</para>
    /// </summary>
    Task<bool> UnstateAbandonedExpectationAsync(RunDataAbandonedExpectation abandoned, CancellationToken cancellationToken);
}

/// <summary>
/// Vector form for a producer whose one durable batch owns several facets. It is deliberately a sibling rather than
/// widening every test double of <see cref="IRunDataCompletenessWriter"/>: production folds the vector under one
/// rendezvous and one database round trip, while a focused double can keep exercising one facet at a time.
/// </summary>
public interface IRunDataCompletenessBatchWriter
{
    Task<bool> AdvanceBatchAsync(IReadOnlyList<RunDataFacetAdvance> advances, CancellationToken cancellationToken);
}

/// <inheritdoc cref="IRunDataCompletenessWriter"/>
public sealed class RunDataCompletenessWriter : IRunDataCompletenessWriter, IRunDataCompletenessBatchWriter, IRunDataAbandonedExpectationWriter, IScopedDependency
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<RunDataCompletenessWriter> _logger;

    public RunDataCompletenessWriter(IServiceScopeFactory scopeFactory, ILogger<RunDataCompletenessWriter> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    public async Task<bool> InitializeAsync(RunDataManifestInitialization initialization, CancellationToken cancellationToken) =>
        await ContainedAsync(LostClaimSubject.Of(initialization.WorkflowRunId), WorkflowRunDataOwnerKinds.DataManifest, async db => await db.Database.ExecuteSqlAsync(
            $"SELECT workflow_run_data_manifest_initialize({initialization.TeamId}, {initialization.WorkflowRunId}, {RunDataManifestCoverage.RequiredFacets.ToArray()}, {WorkflowRunDataContract.CurrentVersion})",
            cancellationToken).ConfigureAwait(false), cancellationToken).ConfigureAwait(false);

    public async Task<bool> AdvanceAsync(RunDataFacetAdvance advance, CancellationToken cancellationToken) =>
        await ContainedAsync(LostClaimSubject.Of(advance.WorkflowRunId), advance.Facet, async db => await db.Database.ExecuteSqlAsync(
            $"SELECT workflow_run_data_manifest_advance_covered({advance.TeamId}, {advance.WorkflowRunId}, {advance.Facet}, {advance.Expected}, {advance.Present}, {advance.Masked}, {RunDataManifestCoverage.RequiredFacets.ToArray()}, {WorkflowRunDataContract.CurrentVersion})",
            cancellationToken).ConfigureAwait(false), cancellationToken).ConfigureAwait(false);

    public async Task<bool> AdvanceBatchAsync(IReadOnlyList<RunDataFacetAdvance> advances, CancellationToken cancellationToken)
    {
        if (advances.Count == 0) return true;

        var first = advances[0];
        if (advances.Any(advance => advance.TeamId != first.TeamId || advance.WorkflowRunId != first.WorkflowRunId))
            throw new ArgumentException("A completeness batch belongs to exactly one tenant-bound workflow run.", nameof(advances));

        return await ContainedAsync(LostClaimSubject.Of(first.WorkflowRunId), WorkflowRunDataOwnerKinds.DataManifest, async db => await db.Database.ExecuteSqlAsync(
            $"SELECT workflow_run_data_manifest_advance_covered_batch({first.TeamId}, {first.WorkflowRunId}, {advances.Select(advance => advance.Facet).ToArray()}, {advances.Select(advance => advance.Expected).ToArray()}, {advances.Select(advance => advance.Present).ToArray()}, {advances.Select(advance => advance.Masked).ToArray()}, {RunDataManifestCoverage.RequiredFacets.ToArray()}, {WorkflowRunDataContract.CurrentVersion})",
            cancellationToken).ConfigureAwait(false), cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> NoticeAsync(WorkflowRunCaptureGap gap, CancellationToken cancellationToken) =>
        await ContainedAsync(LostClaimSubject.Of(gap), gap.SubjectKind, async db =>
        {
            db.WorkflowRunCaptureGap.Add(gap);
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);

    public async Task<bool> UnstateExpectationAsync(Guid teamId, Guid workflowRunId, string facet, CancellationToken cancellationToken)
    {
        var revised = 0L;

        await ContainedAsync(LostClaimSubject.Of(workflowRunId), facet, async db => revised = await Revised(db, teamId, workflowRunId, facet, cancellationToken).ConfigureAwait(false), cancellationToken).ConfigureAwait(false);

        return revised > 0;
    }

    public async Task<bool> UnstateAbandonedExpectationAsync(RunDataAbandonedExpectation abandoned, CancellationToken cancellationToken)
    {
        var revised = 0L;

        await ContainedAsync(LostClaimSubject.Of(abandoned.WorkflowRunId), abandoned.Facet, async db => revised = await RevisedIfStillAbandoned(db, abandoned, cancellationToken).ConfigureAwait(false), cancellationToken).ConfigureAwait(false);

        return revised > 0;
    }

    /// <summary>How many statements the un-stating revised — read back so the caller can name the run it happened to and stay silent when nothing changed.</summary>
    private static async Task<long> Revised(CodeSpaceDbContext db, Guid teamId, Guid workflowRunId, string facet, CancellationToken cancellationToken) =>
        (await db.Database.SqlQuery<long>($"SELECT workflow_run_data_manifest_unstate_expectation({teamId}, {workflowRunId}, {facet}) AS \"Value\"")
            .ToListAsync(cancellationToken).ConfigureAwait(false)).Single();

    /// <summary>The compare-and-set half: 0182 re-asks the caller's whole selecting question under the rendezvous lock, so a row whose answer improved comes back as 0 revised rather than as a destroyed accounting.</summary>
    private static async Task<long> RevisedIfStillAbandoned(CodeSpaceDbContext db, RunDataAbandonedExpectation abandoned, CancellationToken cancellationToken) =>
        (await db.Database.SqlQuery<long>($"SELECT workflow_run_data_manifest_unstate_abandoned_expectation({abandoned.TeamId}, {abandoned.WorkflowRunId}, {abandoned.Facet}, {abandoned.SettledBefore}) AS \"Value\"")
            .ToListAsync(cancellationToken).ConfigureAwait(false)).Single();

    /// <summary>
    /// One write, on this writer's OWN scope, contained. The scope is what keeps a refused claim off the caller's
    /// DbContext, and the containment is what keeps it out of the caller's outcome — with one exception that is not
    /// containment at all: a cancellation while cancellation was requested is the round ending, and re-reporting it as
    /// a lost claim would log noise for every torn-down worker.
    /// </summary>
    private async Task<bool> ContainedAsync(LostClaimSubject subject, string facet, Func<CodeSpaceDbContext, Task> write, CancellationToken cancellationToken)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();

            await write(scope.ServiceProvider.GetRequiredService<CodeSpaceDbContext>()).ConfigureAwait(false);

            return true;
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(exception, "The {Facet} claim of workflow run {WorkflowRunId} / agent run {AgentRunId} could not be stated; the claim is lost, the records it describes are untouched, and the run resolves exactly as it does with no claim at all", facet, subject.WorkflowRunId, subject.AgentRunId);

            return false;
        }
    }

    /// <summary>
    /// WHICH RUN the lost claim was about, carried as one value because either key alone can be the only one present.
    /// A gap may name a standalone Agent Run and no workflow run; every manifest verb names a workflow run and no
    /// agent run. Passing only the workflow run left a standalone gap's warning naming nothing at all — an account of
    /// a lost record that cannot say which record it was, which is the same silence the gap itself was meant to break.
    /// </summary>
    private readonly record struct LostClaimSubject(Guid? WorkflowRunId, Guid? AgentRunId)
    {
        public static LostClaimSubject Of(Guid workflowRunId) => new(workflowRunId, null);

        public static LostClaimSubject Of(WorkflowRunCaptureGap gap) => new(gap.WorkflowRunId, gap.AgentRunId);
    }
}
