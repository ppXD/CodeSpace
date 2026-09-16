using System.Collections.Concurrent;
using CodeSpace.Api.Extensions.Hangfire;
using CodeSpace.Core.Constants;
using CodeSpace.Core.Jobs;
using CodeSpace.E2ETests.Infrastructure;
using Hangfire;
using Hangfire.PostgreSql;
using Hangfire.PostgreSql.Factories;
using Hangfire.States;
using Hangfire.Storage;
using Hangfire.Storage.Monitoring;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Serilog.Core;
using Serilog.Events;
using Shouldly;

namespace CodeSpace.E2ETests.Jobs;

/// <summary>
/// Every <see cref="IRecurringJob"/> in <c>CodeSpace.Core</c>, registered by the real worker host and fired once
/// against real Postgres through the real Hangfire servers.
///
/// <para>Tier: 🟢 High-fidelity (Rule 12) — the production <c>WorkerHangfireRegistrar</c> owns the registration, the
/// production <c>CodeSpaceBackgroundJobClient</c> writes the schedule, two real <c>AddHangfireServer</c> pools fetch
/// from a real Postgres queue, and each tick runs through the production <c>IJobSafeRunner</c> into the mediator
/// pipeline. Nothing on the path under test is substituted.</para>
///
/// <para><b>Why it exists.</b> Recurring jobs are registered ONLY by the Worker role, and until this class no CI lane
/// had ever booted one: both E2E HTTP fixtures pin <c>HangfireHosting=Api</c> (asserted by
/// <c>FactoryHangfireRoleTests</c>) and substitute a job client whose <c>AddOrUpdateRecurringJob</c> is a no-op. So
/// "does a tick complete at all" was measured nowhere, and four sweeps ticked and threw in production unnoticed:
/// budget settlement since 2026-07-14, the stuck-run reconciler and the agent-run spool reaper since 2026-09-07, and
/// lesson distillation since 2026-09-09. <c>SweepCommandTransactionFlowTests</c> (#1980) closed the HANDLER tier by
/// sending each command through the real mediator; this closes the tier above it — registration, dispatch, fetch and
/// execution as a worker pod actually performs them.</para>
///
/// <para><b>What is substituted, honestly.</b> Nothing on the job path. The fixture does make two host-level
/// substitutions, both named in <see cref="RecurringJobWorkerHostFactory"/>: it runs as <c>Development</c> (which
/// relaxes the production boot guards), and it binds the host's own Serilog logger so the log assertion can read
/// what the pipeline wrote. It also sets one process-wide environment variable at init, like its sibling fixtures.</para>
///
/// <para>Deliberately NOT covered: cron TIMING (whether a cadence is right), job payloads, and what any individual
/// sweep does to rows. Cron-fired EXECUTIONS are covered — they land in the same database and are judged by
/// <see cref="AssertNoJobInThisDatabaseFailed"/>. Per-sweep behaviour belongs with each sweep's own tests, which can
/// seed the candidate rows; this measures only that every registered job fires and survives a tick.</para>
/// </summary>
[Trait("Category", "E2E")]
[Trait("Surface", "Worker")]
public sealed class RecurringJobWorkerSmokeE2ETests
{
    /// <summary>
    /// Bound on fetch + execution AFTER the host is up. Sized for the real contention: every triggered tick queues onto
    /// ONE control pool of <c>ControlWorkerCount</c> = 4 workers at a 2s queue poll interval, and the recurring
    /// scheduler adds its own ticks on top — seven jobs are minutely, so over this window they fire two or three extra
    /// times each onto the same four workers. A healthy local run finishes in seconds; the budget exists for a cold CI
    /// runner and is only ever spent when something is actually wrong.
    /// </summary>
    private static readonly TimeSpan TickBudget = TimeSpan.FromMinutes(3);

    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(250);

    /// <summary>Cap on how many failures a red enumerates. A tick that breaks usually breaks every sweep, and thirty stack traces in one message help nobody.</summary>
    private const int MaxFailuresReported = 50;

    /// <summary>The two <c>TransactionalBehavior</c> failure templates, by their rendered tail. A tick can report Succeeded while a command it dispatched rolled back; these lines are the only evidence.</summary>
    private const string RolledBackLine = "failed; transaction rolled back";
    private const string NoTransactionLine = "failed; no transaction to roll back";

    [Fact]
    public async Task Every_recurring_job_the_worker_registers_completes_one_tick()
    {
        var sink = new CapturedLogLines();

        await using var factory = new RecurringJobWorkerHostFactory(sink);
        await factory.InitializeAsync();

        var storage = OpenStorageOnTheFixtureDatabase(factory);
        ProveTheSinkSeesThePipelinesLogger(factory, sink);

        var registered = RegisteredJobIds(storage);
        AssertTheCensusMatches(factory.Services, registered);

        var outcomes = await AwaitEveryTickAsync(storage, TriggerOneTickEach(storage, registered));

        // Triggered ticks first: that assertion names the recurring JOB and quotes its exception. Then everything
        // else in the database, which is how the ticks the CRON scheduler fired on its own get judged by state too.
        // The log lines are last and subtlest — the only evidence left when a tick reports Succeeded and a command
        // dispatched inside it rolled back.
        AssertEveryTickSucceeded(outcomes);
        AssertNoJobInThisDatabaseFailed(storage);
        AssertNothingRolledBack(sink);
    }

    /// <summary>
    /// A storage handle the test OWNS, built over the fixture's own connection string with the production options
    /// (<see cref="HangfireRegistrarBase.BuildStorageOptions"/>). Identity is therefore by construction: every read,
    /// trigger and state poll below is against the database this fixture created, with nothing to assert about it.
    ///
    /// <para>Deliberately NOT <c>factory.Services.GetRequiredService&lt;JobStorage&gt;()</c>. Measured on
    /// Hangfire 1.8.25 / Hangfire.PostgreSql 1.20.13: two containers each keep the storage their own configuration
    /// built, so the DI route would have been correct — but it also re-points the process-wide
    /// <c>JobStorage.Current</c> that <c>CodeSpaceBackgroundJobClient.GetRecurringJobs/GetJobState</c> read
    /// statically, so reading through DI would have coupled this test to a global every other test host in the
    /// process mutates. A second handle onto the same Postgres is also the more independent observation: the host's
    /// servers drain the rows, the test reads them back through its own connection.</para>
    /// </summary>
    private static JobStorage OpenStorageOnTheFixtureDatabase(RecurringJobWorkerHostFactory factory)
    {
        var options = HangfireRegistrarBase.BuildStorageOptions();

        return new PostgreSqlStorage(new NpgsqlConnectionFactory(factory.ConnectionString, options), options);
    }

    /// <summary>
    /// A positive control for the absence assertion. The sink is only trustworthy if the host's own
    /// <c>ILoggerFactory</c> — the one <c>TransactionalBehavior</c> resolves <c>ILogger&lt;T&gt;</c> from — reaches
    /// it; otherwise "no rollback lines" would be true of a sink nothing ever wrote to.
    /// </summary>
    private static void ProveTheSinkSeesThePipelinesLogger(RecurringJobWorkerHostFactory factory, CapturedLogLines sink)
    {
        var canary = $"recurring-job-smoke-canary-{Guid.NewGuid():N}";

        factory.Services.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(RecurringJobWorkerSmokeE2ETests).FullName!).LogWarning("{Canary}", canary);

        sink.Lines().Any(line => line.Contains(canary, StringComparison.Ordinal)).ShouldBeTrue(
            customMessage: "a line written through the HOST's ILoggerFactory never reached the sink, so the rollback-line "
                + "assertion would measure nothing. Check that RecurringJobWorkerHostFactory.CreateHost's UseSerilog is "
                + "still the LAST ILoggerFactory registration on the host builder.");
    }

    private static IReadOnlyList<string> RegisteredJobIds(JobStorage storage)
    {
        using var connection = storage.GetConnection();

        return connection.GetRecurringJobs().Select(job => job.Id).ToList();
    }

    /// <summary>Set equality between the assembly's <see cref="IRecurringJob"/> implementations and Hangfire's recurring set, in all three directions a break can take.</summary>
    private static void AssertTheCensusMatches(IServiceProvider services, IReadOnlyCollection<string> registered)
    {
        var (expected, optedOut) = PartitionByConditionalRegistration(services);

        // Without this the whole census is vacuous: an assembly scan that finds nothing makes every set difference
        // below empty, and a gate that measures nothing passes.
        expected.ShouldNotBeEmpty(
            customMessage: "the scan over CodeSpace.Core found no IRecurringJob implementation at all, so every assertion "
                + "that follows would be trivially true. It is the scan that broke, not the registrar.");

        expected.Except(registered).ToList().ShouldBeEmpty(
            customMessage: "these IRecurringJob implementations exist but the worker registered no schedule for them, so no "
                + "tick of theirs can EVER fire. WorkerHangfireRegistrar.ScanHangfireRecurringJobs skips a job with an empty "
                + "CronExpression (logging an Error) — check the job's own cron first, then its DI registration.");

        optedOut.Intersect(registered).ToList().ShouldBeEmpty(
            customMessage: "these jobs reported IConditionalRecurringJob.ShouldRegister=false yet are scheduled anyway. "
                + "Opting out must mean NO recurring entry at all — a registered entry ticks, which is what the flag exists "
                + "to prevent.");

        registered.Except(expected).Except(optedOut).ToList().ShouldBeEmpty(
            customMessage: "Hangfire holds recurring entries with no IRecurringJob behind them — a renamed or deleted job "
                + "class leaves its old id scheduled (JobId is nameof(class), so a rename mints a new id and strands the "
                + "old one). Remove them with ICodeSpaceBackgroundJobClient.RemoveRecurringJobIfExists.");
    }

    private static (List<string> Expected, List<string> OptedOut) PartitionByConditionalRegistration(IServiceProvider services)
    {
        var expected = new List<string>();
        var optedOut = new List<string>();

        foreach (var type in RecurringJobTypes())
        {
            var job = (IRecurringJob)services.GetRequiredService(type);

            (job is IConditionalRecurringJob { ShouldRegister: false } ? optedOut : expected).Add(job.JobId);
        }

        return (expected, optedOut);
    }

    /// <summary>The same reflection the registrar performs, over the same assembly — the census is only honest if it is taken from the source of truth rather than a hand-maintained list.</summary>
    private static IEnumerable<Type> RecurringJobTypes() =>
        typeof(IRecurringJob).Assembly.GetTypes().Where(type => type.IsClass && !type.IsAbstract && typeof(IRecurringJob).IsAssignableFrom(type));

    /// <summary>Fire one extra execution of each schedule, keyed by recurring job id so a failure can name the job rather than a Hangfire id.</summary>
    private static IReadOnlyDictionary<string, string> TriggerOneTickEach(JobStorage storage, IReadOnlyCollection<string> registered)
    {
        var manager = new RecurringJobManager(storage);

        return registered.ToDictionary(jobId => jobId, jobId => manager.TriggerJob(jobId)
            ?? throw new InvalidOperationException($"Hangfire refused to trigger recurring job {jobId} — its schedule entry vanished between the census and the trigger"));
    }

    /// <summary>Poll each triggered background job until it reaches a terminal state or the budget runs out. The predicate starts false (nothing observed yet), so an empty read can never pass the wait.</summary>
    private static async Task<IReadOnlyList<TickOutcome>> AwaitEveryTickAsync(JobStorage storage, IReadOnlyDictionary<string, string> ticks)
    {
        var deadline = DateTime.UtcNow + TickBudget;
        var latest = ticks.ToDictionary(tick => tick.Key, _ => (StateData?)null);

        while (!latest.Values.All(IsTerminal) && DateTime.UtcNow < deadline)
        {
            await Task.Delay(PollInterval);

            using var connection = storage.GetConnection();

            foreach (var recurringJobId in latest.Keys.Where(id => !IsTerminal(latest[id])).ToList())
                latest[recurringJobId] = connection.GetStateData(ticks[recurringJobId]);
        }

        return ticks.Select(tick => new TickOutcome(tick.Key, tick.Value, latest[tick.Key])).ToList();
    }

    /// <summary>Hangfire's own state names, not string literals — a retry would park the job in Scheduled, which is deliberately NOT terminal here (the registrar disables retries, so a Scheduled tick is itself a finding).</summary>
    private static bool IsTerminal(StateData? state) =>
        state != null && (state.Name == SucceededState.StateName || state.Name == FailedState.StateName || state.Name == DeletedState.StateName);

    private static void AssertEveryTickSucceeded(IReadOnlyList<TickOutcome> outcomes)
    {
        outcomes.Where(outcome => outcome.State?.Name != SucceededState.StateName).Select(Describe).ToList().ShouldBeEmpty(
            customMessage: $"every registered recurring job must survive one tick. A '{FailedState.StateName}' entry above quotes the tick's "
                + "own exception — that is a sweep throwing through the mediator pipeline, the shape that kept four of them broken in "
                + $"production. No state at all means no control-plane worker fetched it within {TickBudget:g}: check that "
                + $"WorkerHangfireRegistrar still calls AddHangfireServer on the '{HangfireConstants.DefaultQueue}' queue, then re-run this "
                + "class alone with: dotnet test backend/tests/CodeSpace.E2ETests/CodeSpace.E2ETests.csproj --filter \"Category=E2E&Surface=Worker\"");
    }

    /// <summary>
    /// No background job in this database failed — the triggered ticks AND everything the recurring scheduler fired
    /// on its own meanwhile. The database is created per test and dropped with it, so every row in Hangfire's failed
    /// set belongs to this run; no "since when" filter is needed or possible to get wrong.
    ///
    /// <para>Without this, a cron-fired tick was judged only by <see cref="AssertNothingRolledBack"/> — its Error
    /// line landed in the sink while the state assertion, which only knows the ids the census triggered, could not
    /// see it. Seven jobs are minutely, so over the tick budget that is a real population, not a corner case.</para>
    /// </summary>
    private static void AssertNoJobInThisDatabaseFailed(JobStorage storage)
    {
        var failures = storage.GetMonitoringApi().FailedJobs(0, MaxFailuresReported).Select(DescribeFailure).ToList();

        failures.ShouldBeEmpty(
            customMessage: "a background job in this test's own database ended Failed. If its id is not among the ticks this test "
                + "triggered, the recurring SCHEDULER fired it on cron while the test ran — the same job, the same defect, just a "
                + "tick nobody asked for. The exception is quoted above; retries are off (AutomaticRetry Attempts=0), so a Failed "
                + "entry is the tick's own first and only outcome.");
    }

    private static string DescribeFailure(KeyValuePair<string, FailedJobDto> failure) =>
        $"background job {failure.Key} ({RecurringJobIdOf(failure.Value)}) failed: {failure.Value.ExceptionType} — {failure.Value.ExceptionMessage}";

    /// <summary>The registrar schedules <c>IJobSafeRunner.Run(jobId, jobType)</c>, so the first serialised argument names the recurring job.</summary>
    private static string RecurringJobIdOf(FailedJobDto failure) =>
        failure.Job?.Args is { Count: > 0 } args ? args[0]?.ToString() ?? "<null job id>" : "<job payload could not be loaded>";

    private static string Describe(TickOutcome outcome) =>
        $"{outcome.RecurringJobId} (background job {outcome.BackgroundJobId}) ended in state '{outcome.State?.Name ?? "<none — never fetched>"}'{Cause(outcome.State)}";

    private static string Cause(StateData? state)
    {
        if (state?.Data == null) return string.Empty;

        state.Data.TryGetValue("ExceptionType", out var type);
        state.Data.TryGetValue("ExceptionMessage", out var message);

        return message == null ? $" ({state.Reason})" : $": {type} — {message}";
    }

    private static void AssertNothingRolledBack(CapturedLogLines sink)
    {
        var failures = sink.Lines()
            .Where(line => line.Contains(RolledBackLine, StringComparison.Ordinal) || line.Contains(NoTransactionLine, StringComparison.Ordinal))
            .ToList();

        failures.ShouldBeEmpty(
            customMessage: "TransactionalBehavior logged a command failure while the sweeps ran. A tick reports Succeeded even when a "
                + "command dispatched INSIDE it rolled back, so these lines are the only evidence — and they are exactly what nobody was "
                + "reading while budget settlement, the stuck-run reconciler, the spool reaper and lesson distillation ticked and threw "
                + "in production. The line names the command; its handler is where to look. It may belong to a tick the recurring "
                + "scheduler fired on cron rather than one this test triggered; the state assertion above covers those too, so a line "
                + "here with no failed job means the failure was swallowed inside a tick that still reported Succeeded.");
    }

    /// <summary>One triggered execution: which schedule asked for it, which background job carried it, and the last state observed.</summary>
    private sealed record TickOutcome(string RecurringJobId, string BackgroundJobId, StateData? State);

    /// <summary>Serilog sink that keeps every rendered line the host writes at Warning or above, so the test can assert on what the pipeline logged.</summary>
    private sealed class CapturedLogLines : ILogEventSink
    {
        private readonly ConcurrentQueue<string> _lines = new();

        public void Emit(LogEvent logEvent) => _lines.Enqueue(logEvent.RenderMessage());

        public IReadOnlyList<string> Lines() => _lines.ToArray();
    }
}
