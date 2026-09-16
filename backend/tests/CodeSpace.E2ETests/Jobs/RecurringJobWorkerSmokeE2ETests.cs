using System.Collections.Concurrent;
using CodeSpace.Core.Constants;
using CodeSpace.Core.Jobs;
using CodeSpace.E2ETests.Infrastructure;
using Hangfire;
using Hangfire.States;
using Hangfire.Storage;
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
/// <para>Deliberately NOT covered: cron timing, job payloads, and what any individual sweep does to rows. Per-sweep
/// behaviour belongs with each sweep's own tests, which can seed the candidate rows; this measures only that every
/// registered job fires and survives one tick.</para>
/// </summary>
[Trait("Category", "E2E")]
[Trait("Surface", "Worker")]
public sealed class RecurringJobWorkerSmokeE2ETests
{
    /// <summary>Bound on fetch + execution AFTER the host is up. Generous on purpose: every sweep shares one 4-worker control pool whose queue poll interval is 2s, and a cold CI runner is slow. A healthy run finishes in seconds; the budget is only ever spent when something is wrong.</summary>
    private static readonly TimeSpan TickBudget = TimeSpan.FromMinutes(3);

    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(250);

    /// <summary>The two <c>TransactionalBehavior</c> failure templates, by their rendered tail. A tick can report Succeeded while a command it dispatched rolled back; these lines are the only evidence.</summary>
    private const string RolledBackLine = "failed; transaction rolled back";
    private const string NoTransactionLine = "failed; no transaction to roll back";

    [Fact]
    public async Task Every_recurring_job_the_worker_registers_completes_one_tick()
    {
        var sink = new CapturedLogLines();

        await using var factory = new RecurringJobWorkerHostFactory(sink);
        await factory.InitializeAsync();

        var storage = StorageOfThisHost(factory);
        ProveTheSinkSeesThePipelinesLogger(factory, sink);

        var registered = RegisteredJobIds(storage);
        AssertTheCensusMatches(factory.Services, registered);

        var outcomes = await AwaitEveryTickAsync(storage, TriggerOneTickEach(storage, registered));

        // Job state first: it names the recurring JOB and quotes the exception. The log lines are the subtler
        // signal — they are the only evidence left when a tick reports Succeeded and a command it dispatched
        // rolled back inside it.
        AssertEveryTickSucceeded(outcomes);
        AssertNothingRolledBack(sink);
    }

    /// <summary>
    /// Hangfire's DI registration reads the process-wide <c>JobStorage.Current</c>, which EVERY
    /// <c>WebApplicationFactory</c> boot in this process re-points. Naming the database turns a lost race into a
    /// legible red rather than a census silently taken against another fixture's storage.
    /// </summary>
    private static JobStorage StorageOfThisHost(RecurringJobWorkerHostFactory factory)
    {
        var storage = factory.Services.GetRequiredService<JobStorage>();

        storage.ToString().ShouldContain(factory.DatabaseName,
            customMessage: $"the resolved Hangfire storage is not this host's database ({factory.DatabaseName}) — another test "
                + "host re-pointed the process-wide JobStorage.Current mid-boot. Run this class in its own process; the "
                + "Surface=Worker CI job does exactly that.");

        return storage;
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
                + "in production. The line names the command; its handler is where to look.");
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
