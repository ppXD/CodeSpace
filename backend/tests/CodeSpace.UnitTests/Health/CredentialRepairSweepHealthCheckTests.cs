using CodeSpace.Core.Jobs.RecurringJobs;
using CodeSpace.Core.Services.Credentials;
using CodeSpace.Core.Services.Health.Checks;
using Hangfire;
using Hangfire.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Shouldly;

namespace CodeSpace.UnitTests.Health;

/// <summary>
/// Whether the credential binding-repair sweep exists on this deployment at all, and how big the backlog it should be
/// clearing is. Only a Worker-role pod registers it, so an API-only fleet — or workers still on a build from before the sweep —
/// has none, and nothing else says so. The schedule is read the way Hangfire records it; the entry here is built with the
/// fields a recurring job really carries, and the integration tier reads one back from real storage.
/// </summary>
[Trait("Category", "Unit")]
public sealed class CredentialRepairSweepHealthCheckTests
{
    private static readonly DateTime Now = new(2026, 10, 18, 9, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void The_check_is_named_for_the_sweep_and_looks_for_the_job_the_worker_registers()
    {
        CredentialRepairSweepHealthCheck.Name.ShouldBe("credential-repair-sweep");
        CredentialRepairSweepHealthCheck.JobId.ShouldBe(nameof(DisconnectedCredentialBindingRepairRecurringJob), "Hangfire indexes the schedule by this id; the registration and this lookup must agree");
    }

    [Fact]
    public void A_scheduled_sweep_that_ran_a_few_minutes_ago_is_healthy_and_reports_when_and_how_many()
    {
        var entry = Entry(lastExecution: Now.AddMinutes(-3), nextExecution: Now.AddMinutes(2), lastJobState: "Succeeded");

        var result = CredentialRepairSweepHealthCheck.Assess(entry, candidates: 0, Now);

        result.Status.ShouldBe(HealthStatus.Healthy);
        result.Data["registered"].ShouldBe(true);
        result.Data["lastExecutionUtc"].ShouldBe(Now.AddMinutes(-3));
        result.Data["nextExecutionUtc"].ShouldBe(Now.AddMinutes(2));
        result.Data["lastJobState"].ShouldBe("Succeeded");
        result.Data["cron"].ShouldBe("*/5 * * * *");
        result.Data["strandedCandidates"].ShouldBe(0);
    }

    [Fact]
    public void A_sweep_registered_a_moment_ago_that_has_not_run_yet_is_healthy_not_missing()
    {
        var result = CredentialRepairSweepHealthCheck.Assess(Entry(lastExecution: null, nextExecution: Now.AddMinutes(4), lastJobState: null), candidates: 2, Now);

        result.Status.ShouldBe(HealthStatus.Healthy);
        result.Data["strandedCandidates"].ShouldBe(2);
        result.Data.ShouldNotContainKey("lastExecutionUtc");
        result.Description.ShouldNotBeNull().ShouldContain("not run yet");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_sweep_nothing_registered_is_degraded_and_says_which_pods_register_it(bool removedMarker)
    {
        // Hangfire answers a recurring id it holds no entry for with a placeholder marked Removed, not with null.
        RecurringJobDto? entry = removedMarker ? new RecurringJobDto { Id = CredentialRepairSweepHealthCheck.JobId, Removed = true } : null;

        var result = CredentialRepairSweepHealthCheck.Assess(entry, candidates: 3, Now);

        result.Status.ShouldBe(HealthStatus.Degraded);
        result.Description.ShouldNotBeNull().ShouldContain("not registered");
        result.Description.ShouldContain("Worker", customMessage: "only the Worker role registers recurring jobs");
        result.Data["registered"].ShouldBe(false);
        result.Data["strandedCandidates"].ShouldBe(3, "the backlog is still the answer to how bad it is");
    }

    [Fact]
    public void A_sweep_the_scheduler_disabled_is_degraded_and_says_an_error_was_recorded_without_repeating_it()
    {
        // A previous-version scheduler that cannot load a job type retries, then disables the entry: no next execution.
        var entry = Entry(lastExecution: Now.AddHours(-1), nextExecution: null, lastJobState: "Succeeded");
        entry.Error = "Could not load type 'CodeSpace.Core.Jobs.IJobSafeRunner'";

        var result = CredentialRepairSweepHealthCheck.Assess(entry, candidates: 0, Now);

        result.Status.ShouldBe(HealthStatus.Degraded);
        result.Description.ShouldNotBeNull().ShouldContain("disabled");
        result.Description.ShouldContain("recorded an error", customMessage: "it says there is one, and where to read it");
        result.Description.ShouldNotContain("Could not load type", customMessage: "the scheduler's text is an exception chain: it stays in the log, not on the report");
    }

    [Fact]
    public void A_scheduler_error_with_stack_frames_and_a_storage_address_never_reaches_the_description_or_the_data()
    {
        // What Hangfire's RecurringJobScheduler stores for a storage fault: the exception chain, up to a hundred frames, host:port included.
        var entry = Entry(lastExecution: null, nextExecution: null, lastJobState: null);
        entry.Error = "Npgsql.NpgsqlException: Failed to connect to 10.20.30.40:5432\n ---> System.Net.Sockets.SocketException (111): Connection refused\n   at Npgsql.Internal.NpgsqlConnector.Connect(NpgsqlTimeout timeout)\n   at Hangfire.PostgreSql.PostgreSqlJobQueue.Enqueue(String queue, String jobId)";

        var result = CredentialRepairSweepHealthCheck.Assess(entry, candidates: 0, Now);
        var everythingPrinted = result.Description + string.Join(' ', result.Data.Select(pair => $"{pair.Key}={pair.Value}"));

        everythingPrinted.ShouldNotContain("10.20.30.40");
        everythingPrinted.ShouldNotContain("Npgsql");
        everythingPrinted.ShouldNotContain("Hangfire.PostgreSql");
        result.Description.ShouldNotContain("\n");
    }

    [Fact]
    public void A_sweep_whose_next_run_is_long_past_is_degraded_because_no_scheduler_is_running()
    {
        var entry = Entry(lastExecution: Now.AddMinutes(-40), nextExecution: Now.AddMinutes(-35), lastJobState: "Succeeded");

        var result = CredentialRepairSweepHealthCheck.Assess(entry, candidates: 1, Now);

        result.Status.ShouldBe(HealthStatus.Degraded);
        result.Description.ShouldNotBeNull().ShouldContain("overdue");
        result.Description.ShouldContain("35");
    }

    [Fact]
    public void A_next_run_a_little_late_is_still_healthy_the_scheduler_polls_between_ticks()
    {
        var result = CredentialRepairSweepHealthCheck.Assess(Entry(lastExecution: Now.AddMinutes(-5), nextExecution: Now.AddSeconds(-30), lastJobState: "Succeeded"), candidates: 0, Now);

        result.Status.ShouldBe(HealthStatus.Healthy);
    }

    [Fact]
    public void A_sweep_whose_last_run_failed_is_degraded()
    {
        var result = CredentialRepairSweepHealthCheck.Assess(Entry(lastExecution: Now.AddMinutes(-1), nextExecution: Now.AddMinutes(4), lastJobState: "Failed"), candidates: 0, Now);

        result.Status.ShouldBe(HealthStatus.Degraded);
        result.Description.ShouldNotBeNull().ShouldContain("failed");
    }

    [Fact]
    public void A_backlog_that_could_not_be_counted_is_degraded_and_the_schedule_is_still_reported()
    {
        var result = CredentialRepairSweepHealthCheck.Assess(Entry(lastExecution: Now.AddMinutes(-3), nextExecution: Now.AddMinutes(2), lastJobState: "Succeeded"), candidates: null, Now);

        result.Status.ShouldBe(HealthStatus.Degraded);
        result.Description.ShouldNotBeNull().ShouldContain("could not be counted");
        result.Data.ShouldNotContainKey("strandedCandidates");
        result.Data["registered"].ShouldBe(true);
    }

    [Fact]
    public async Task A_host_with_no_job_storage_says_so_instead_of_throwing()
    {
        var check = new CredentialRepairSweepHealthCheck(new NoCensusScopes(), TimeProvider.System, NullLogger<CredentialRepairSweepHealthCheck>.Instance, storage: null);

        var result = await check.CheckHealthAsync(new HealthCheckContext(), CancellationToken.None);

        result.Status.ShouldBe(HealthStatus.Degraded);
        result.Description.ShouldNotBeNull().ShouldContain("job storage");
    }

    [Fact]
    public async Task Storage_that_cannot_be_read_degrades_the_report_by_exception_type_and_never_throws()
    {
        var storage = new DownStorage();
        var check = new CredentialRepairSweepHealthCheck(new NoCensusScopes(), TimeProvider.System, NullLogger<CredentialRepairSweepHealthCheck>.Instance, storage);

        var result = await check.CheckHealthAsync(new HealthCheckContext(), CancellationToken.None);

        result.Status.ShouldBe(HealthStatus.Degraded);
        result.Description.ShouldNotBeNull().ShouldContain(nameof(InvalidOperationException));
        result.Description.ShouldNotContain("10.9.8.7", customMessage: "/health is anonymous: an address from an exception message must stay out of it");
    }

    [Fact]
    public async Task A_reading_is_reused_for_its_lifetime_so_an_anonymous_endpoint_cannot_make_the_database_work_per_request()
    {
        var clock = new FakeTimeProvider(Now);
        var storage = new DownStorage();
        var check = new CredentialRepairSweepHealthCheck(new NoCensusScopes(), clock, NullLogger<CredentialRepairSweepHealthCheck>.Instance, storage);

        var first = await check.CheckHealthAsync(new HealthCheckContext(), CancellationToken.None);
        clock.Advance(CredentialRepairSweepHealthCheck.SnapshotLifetime - TimeSpan.FromSeconds(1));
        var reused = await check.CheckHealthAsync(new HealthCheckContext(), CancellationToken.None);

        reused.Description.ShouldBe(first.Description);
        storage.Opened.ShouldBe(1);

        clock.Advance(TimeSpan.FromSeconds(1));
        await check.CheckHealthAsync(new HealthCheckContext(), CancellationToken.None);

        storage.Opened.ShouldBe(2, "an aged-out reading is taken again");
    }

    [Fact]
    public void The_schedulers_error_is_logged_in_full_the_first_time_it_is_seen_and_not_again_while_it_stays_the_same()
    {
        var logger = new CapturingLogger();
        var check = new CredentialRepairSweepHealthCheck(new NoCensusScopes(), TimeProvider.System, logger, storage: null);
        var disabled = Entry(lastExecution: null, nextExecution: null, lastJobState: null);
        disabled.Error = "Npgsql.NpgsqlException: Failed to connect to 10.20.30.40:5432\n   at Hangfire.PostgreSql.PostgreSqlJobQueue.Enqueue";

        check.LogSchedulerError(disabled);
        check.LogSchedulerError(disabled);

        var warning = logger.Entries.ShouldHaveSingleItem("a read every thirty seconds must not repeat the same Warning");

        warning.Level.ShouldBe(LogLevel.Warning);
        warning.Message.ShouldContain("10.20.30.40:5432", customMessage: "the log is where the full text, address and frames included, goes");
        warning.Message.ShouldContain("PostgreSqlJobQueue.Enqueue");
    }

    [Fact]
    public void A_different_error_and_an_error_that_returns_after_the_entry_recovered_are_each_logged_again()
    {
        var logger = new CapturingLogger();
        var check = new CredentialRepairSweepHealthCheck(new NoCensusScopes(), TimeProvider.System, logger, storage: null);
        var first = Entry(lastExecution: null, nextExecution: null, lastJobState: null);
        first.Error = "first failure";
        var second = Entry(lastExecution: null, nextExecution: null, lastJobState: null);
        second.Error = "second failure";
        var healthy = Entry(lastExecution: Now, nextExecution: Now.AddMinutes(5), lastJobState: "Succeeded");

        check.LogSchedulerError(first);
        check.LogSchedulerError(second);
        check.LogSchedulerError(healthy);
        check.LogSchedulerError(second);

        logger.Entries.Select(e => e.Message).ShouldBe(new[]
        {
            "Hangfire's scheduler disabled the DisconnectedCredentialBindingRepairRecurringJob schedule after an error: first failure",
            "Hangfire's scheduler disabled the DisconnectedCredentialBindingRepairRecurringJob schedule after an error: second failure",
            "Hangfire's scheduler disabled the DisconnectedCredentialBindingRepairRecurringJob schedule after an error: second failure",
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void An_entry_with_no_error_text_or_none_registered_logs_nothing(bool removedMarker)
    {
        var logger = new CapturingLogger();
        var check = new CredentialRepairSweepHealthCheck(new NoCensusScopes(), TimeProvider.System, logger, storage: null);

        check.LogSchedulerError(removedMarker ? new RecurringJobDto { Id = CredentialRepairSweepHealthCheck.JobId, Removed = true } : Entry(lastExecution: null, nextExecution: null, lastJobState: null));
        check.LogSchedulerError(null);

        logger.Entries.ShouldBeEmpty();
    }

    private sealed class CapturingLogger : ILogger<CredentialRepairSweepHealthCheck>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, formatter(state, exception)));
    }

    /// <summary>A recurring entry as Hangfire materializes one: the cron, the dates it keeps in UTC, and the last job's state.</summary>
    private static RecurringJobDto Entry(DateTime? lastExecution, DateTime? nextExecution, string? lastJobState) => new()
    {
        Id = CredentialRepairSweepHealthCheck.JobId,
        Cron = "*/5 * * * *",
        LastExecution = lastExecution,
        NextExecution = nextExecution,
        LastJobState = lastJobState,
        CreatedAt = Now.AddDays(-1),
    };

    /// <summary>Job storage whose every read fails, the way an unreachable database does — with an address in the message.</summary>
    private sealed class DownStorage : JobStorage
    {
        public int Opened { get; private set; }

        public override IMonitoringApi GetMonitoringApi() => throw new NotSupportedException();

        public override IStorageConnection GetConnection()
        {
            Opened++;

            throw new InvalidOperationException("storage is down at 10.9.8.7:5432");
        }
    }

    private sealed class NoCensusScopes : IServiceScopeFactory
    {
        public IServiceScope CreateScope() => throw new InvalidOperationException("no storage means no census is attempted");
    }
}
