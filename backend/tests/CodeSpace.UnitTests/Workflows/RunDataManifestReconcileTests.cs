using CodeSpace.Core.Handlers.CommandHandlers.Workflows;
using CodeSpace.Core.Jobs;
using CodeSpace.Core.Jobs.RecurringJobs;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.RunData;
using CodeSpace.Messages.Commands.Workflows;
using CodeSpace.Messages.Contracts;
using Cronos;
using MediatR;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace CodeSpace.UnitTests.Workflows;

/// <summary>
/// The manifest plane's reconciler, at the two seams a database cannot cover: WHICH statements it picks up, and the
/// wiring that makes it run at all.
///
/// <para>The predicate is the load-bearing half. Every producer declares its expectation before the records land, so
/// <c>present &lt; expected</c> on a run that is still going is the NORMAL shape of a facet mid-flight — and on a
/// terminal run whose gap plane says nothing, it is a shortfall nobody attributed to anything. That second case is what
/// gets un-stated. The three ways it must NOT fire are pinned beside it, because each one trades a better answer for a
/// worse one: an attributed shortfall already knows what is missing, a facet still settling may yet be accounted for,
/// and one already indeterminate would only churn a revision.</para>
/// </summary>
[Trait("Category", "Unit")]
public sealed class RunDataManifestReconcileTests
{
    /// <summary>
    /// A statement is picked up only when the shortfall is real, unattributed, and has stopped moving. Every FALSE row
    /// here is a statement the sweep must leave exactly as it found it — un-stating latches, so a facet taken too early
    /// can never be walked back to the complete record it was about to become.
    ///
    /// <para>The last row is the odd one and is deliberate: a complete verdict over a determinate expectation the
    /// present count falls short of is UNREACHABLE in the database, because
    /// <c>ck_workflow_run_data_manifest_completeness</c> refuses exactly that row. The conjunct is carried anyway —
    /// it is what lets the batch query be served by the partial index over incomplete statements — and this row is
    /// what stops someone deleting it as redundant without noticing that the plan depends on it.</para>
    /// </summary>
    [Theory]
    [InlineData(3L, 1L, 0L, -1, WorkflowRunCaptureCompleteness.Partial, true)]
    [InlineData(3L, 3L, 0L, -1, WorkflowRunCaptureCompleteness.Partial, false)]
    [InlineData(3L, 4L, 0L, -1, WorkflowRunCaptureCompleteness.Partial, false)]
    [InlineData(null, 1L, 0L, -1, WorkflowRunCaptureCompleteness.LegacyUnknown, false)]
    [InlineData(3L, 1L, 1L, -1, WorkflowRunCaptureCompleteness.Partial, false)]
    [InlineData(3L, 1L, 0L, 1, WorkflowRunCaptureCompleteness.Partial, false)]
    [InlineData(3L, 1L, 0L, -1, WorkflowRunCaptureCompleteness.Exact, false)]
    public void Only_an_unattributed_shortfall_that_has_stopped_moving_is_picked_up(long? expected, long present, long knownMissing, int minutesFromCutoff, WorkflowRunCaptureCompleteness verdict, bool picked)
    {
        var settledBefore = DateTimeOffset.UtcNow;
        var statement = new WorkflowRunDataManifest
        {
            Facet = WorkflowRunDataOwnerKinds.Deliverable,
            ExpectedRecordCount = expected,
            PresentRecordCount = present,
            KnownMissingCount = knownMissing,
            Verdict = verdict,
            LastModifiedAt = settledBefore.AddMinutes(minutesFromCutoff),
        };

        RunDataManifestReconciler.UnattributedShortfall(settledBefore).Compile()(statement).ShouldBe(picked);
    }

    /// <summary>
    /// The window is what separates "the producer is gone" from "the accounting is a second behind". Producers state
    /// completeness on their OWN contained unit of work, off the run's transaction, so an accounting can commit after
    /// the run terminalizes — and the un-stating it would race is permanent. It must therefore be at least one whole
    /// tick of the job below, or the sweep can reach a facet no tick ever gave a chance to finish.
    ///
    /// <para>The cadence is READ OFF the job's own cron rather than restated here, because a number written twice is
    /// two numbers: a cron loosened to the half hour with the window left at a quarter would leave every facet
    /// reachable half a tick after its last advance, and an assertion that had memorised fifteen minutes would go on
    /// agreeing with itself about a schedule nothing runs on.</para>
    /// </summary>
    [Fact]
    public void A_facet_is_given_at_least_a_full_tick_of_silence_before_it_is_un_stated()
    {
        var cadence = CadenceOf(new RunDataManifestReconcilerRecurringJob(new RecordingMediator()).CronExpression);

        RunDataManifestReconciler.SettlingWindow.ShouldBeGreaterThanOrEqualTo(cadence,
            $"the settling window is {RunDataManifestReconciler.SettlingWindow} but the sweep only runs every {cadence}, so a facet can be un-stated without one whole tick having passed since its last advance — and the accounting it races is permanently gone");
    }

    /// <summary>The gap between two consecutive firings of the schedule Hangfire will actually run, computed by the same cron library Hangfire uses rather than read off the string by eye.</summary>
    private static TimeSpan CadenceOf(string cron)
    {
        var schedule = CronExpression.Parse(cron);
        var first = schedule.GetNextOccurrence(DateTime.UtcNow, TimeZoneInfo.Utc)!.Value;

        return schedule.GetNextOccurrence(first, TimeZoneInfo.Utc)!.Value - first;
    }

    /// <summary>
    /// The wiring, pinned for the same reasons as every other sweep's: Hangfire indexes the schedule by
    /// <c>JobId</c>, so a rename strands the old entry and the sweep silently stops; a cron a digit off turns a
    /// quarter-hour into a quarter-day; and a dispatched command of the wrong type misses the mediator pipeline the job
    /// exists to enter.
    /// </summary>
    [Fact]
    public async Task The_recurring_job_is_a_thin_quarter_hourly_dispatcher_for_the_bounded_sweep()
    {
        var mediator = new RecordingMediator();
        var job = new RunDataManifestReconcilerRecurringJob(mediator);

        typeof(RunDataManifestReconcilerRecurringJob).GetInterfaces().ShouldContain(typeof(IRecurringJob));
        job.JobId.ShouldBe(nameof(RunDataManifestReconcilerRecurringJob));
        job.CronExpression.ShouldBe("*/15 * * * *");

        await job.Execute();

        mediator.Sent.ShouldHaveSingleItem().ShouldBeOfType<ReconcileRunDataManifestsCommand>();
    }

    [Fact]
    public async Task The_handler_spends_exactly_one_bounded_pass_and_reports_what_it_saw()
    {
        var reconciler = new RecordingReconciler();

        var response = await new ReconcileRunDataManifestsCommandHandler(reconciler).Handle(new ReconcileRunDataManifestsCommand(), CancellationToken.None);

        reconciler.BatchSizes.ShouldHaveSingleItem().ShouldBe(100,
            "one tick of a deployment-wide sweep must be bounded, or a backlog spends an unbounded number of un-statings before anything else runs");

        // Distinct values on purpose: a transposed mapping would leave an operator reading an un-stated count where the
        // unchanged one belongs, which is the difference between "the sweep is working" and "the sweep found nothing".
        response.Examined.ShouldBe(9);
        response.Unstated.ShouldBe(7);
        response.Unchanged.ShouldBe(2);
    }

    /// <summary>
    /// The sweep's OWN accounting, over a batch this test chose. Every candidate the pass picked up is offered to the
    /// conditional writer exactly once and lands in exactly one arm — a refusal is the ordinary answer for a row whose
    /// answer improved between the selecting read and the write, and counting one as an un-stating would tell an
    /// operator the sweep is closing shortfalls it is in fact leaving alone.
    ///
    /// <para>Held HERE and not against a real pass. The batch comes from a deployment-wide query over a database whose
    /// unattributed shortfalls belong to whoever else is running, so a real pass's <c>Examined</c> is nobody's
    /// arithmetic to assert — it counts strangers' rows and a bounded batch can leave out the seeded one entirely.
    /// These are the same three numbers over a set the reconciler is handed, which is where the arithmetic lives.</para>
    /// </summary>
    [Fact]
    public async Task Every_candidate_a_pass_picks_up_is_counted_in_exactly_one_arm()
    {
        var batch = Enumerable.Range(0, 5).Select(_ => Candidate()).ToList();
        var writer = new RecordingUnstater(candidate => batch.IndexOf(candidate) < 3);

        var reconciliation = await Reconciler(writer).UnstateEachAsync(batch, CancellationToken.None);

        writer.Offered.ShouldBe(batch, "a candidate the read picked up and the pass never offered to the writer is a shortfall the sweep skipped while reporting that it examined it");

        // Distinct values on purpose: a transposed or double-counted arm reads as a working sweep either way.
        reconciliation.Examined.ShouldBe(5, "every candidate the batch handed over was examined, whatever the writer then answered about it");
        reconciliation.Unstated.ShouldBe(3, "only the writer's own yes is an un-stating; a refusal is the row's answer improving before the write, not a facet this pass made indeterminate");
        reconciliation.Unchanged.ShouldBe(2, "the refused candidates are the ones left exactly as found, and an operator reading zero here would take a sweep that is racing producers for a sweep that never has to");
    }

    /// <summary>One facet a read picked up as abandoned. The identity is what the arms are decided by, so every candidate gets its own run.</summary>
    private static RunDataAbandonedExpectation Candidate() => new()
    {
        TeamId = Guid.NewGuid(), WorkflowRunId = Guid.NewGuid(), Facet = WorkflowRunDataOwnerKinds.Deliverable, SettledBefore = DateTimeOffset.UtcNow,
    };

    /// <summary>The real reconciler with only the seam that decides each arm real. The batch is handed in, so the DbContext its selecting read would need is never reached — and an accounting that started reading the database here should say so loudly.</summary>
    private static RunDataManifestReconciler Reconciler(IRunDataAbandonedExpectationWriter writer) =>
        new(null!, writer, TimeProvider.System, NullLogger<RunDataManifestReconciler>.Instance);

    /// <summary>Answers each candidate the way the database would — revised, or refused because the row stopped qualifying — and remembers what it was asked about.</summary>
    private sealed class RecordingUnstater(Func<RunDataAbandonedExpectation, bool> revised) : IRunDataAbandonedExpectationWriter
    {
        public List<RunDataAbandonedExpectation> Offered { get; } = [];

        public Task<bool> UnstateAbandonedExpectationAsync(RunDataAbandonedExpectation abandoned, CancellationToken cancellationToken)
        {
            Offered.Add(abandoned);

            return Task.FromResult(revised(abandoned));
        }
    }

    private sealed class RecordingReconciler : IRunDataManifestReconciler
    {
        public List<int> BatchSizes { get; } = [];

        public Task<RunDataManifestReconciliation> ReconcileUnattributedShortfallsAsync(int batchSize, CancellationToken cancellationToken)
        {
            BatchSizes.Add(batchSize);

            return Task.FromResult(new RunDataManifestReconciliation { Examined = 9, Unstated = 7, Unchanged = 2 });
        }
    }

    private sealed class RecordingMediator : IMediator
    {
        public List<object> Sent { get; } = [];

        public Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
        {
            Sent.Add(request);
            return Task.FromResult(default(TResponse)!);
        }

        public Task<object?> Send(object request, CancellationToken cancellationToken = default)
        {
            Sent.Add(request);
            return Task.FromResult<object?>(null);
        }

        public Task Send<TRequest>(TRequest request, CancellationToken cancellationToken = default) where TRequest : IRequest
        {
            Sent.Add(request);
            return Task.CompletedTask;
        }

        public IAsyncEnumerable<TResponse> CreateStream<TResponse>(IStreamRequest<TResponse> request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public IAsyncEnumerable<object?> CreateStream(object request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task Publish(object notification, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task Publish<TNotification>(TNotification notification, CancellationToken cancellationToken = default) where TNotification : INotification => throw new NotSupportedException();
    }
}
