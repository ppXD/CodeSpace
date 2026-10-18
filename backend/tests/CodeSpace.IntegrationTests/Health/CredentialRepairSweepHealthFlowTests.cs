using Autofac;
using Autofac.Extensions.DependencyInjection;
using CodeSpace.Api.Extensions.Hangfire;
using CodeSpace.Core.Services.Credentials;
using CodeSpace.Core.Services.Health.Checks;
using CodeSpace.Core.Services.Jobs;
using CodeSpace.IntegrationTests.Fleet;
using CodeSpace.IntegrationTests.Infrastructure;
using Hangfire;
using Hangfire.Common;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace CodeSpace.IntegrationTests.Health;

/// <summary>
/// The credential repair sweep's health entry read from real Hangfire storage on Postgres, over the schedule the production
/// <c>RecurringJobSchedule</c> writes — so what the check reads is what a Worker pod leaves behind, field for field, and an
/// API pod's report describes it. The backlog is the production census over the fixture's database.
///
/// <para>Tier: 🟢 High-fidelity (Rule 12) for the schedule and the backlog. What no test here can produce is a worker's
/// scheduler actually firing the entry (that is <c>RecurringJobWorkerSmokeE2ETests</c>), so a "last run" is read only from
/// the unit tier; here the schedule's next run is moved the way time moves it.</para>
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public sealed class CredentialRepairSweepHealthFlowTests
{
    private readonly PostgresFixture _fixture;

    public CredentialRepairSweepHealthFlowTests(PostgresFixture fixture) { _fixture = fixture; }

    [Fact]
    public async Task A_schedule_a_worker_registered_reads_back_healthy_with_its_cadence_and_the_live_backlog()
    {
        using var fleet = new HangfireFleetHarness(_fixture.ConnectionString);
        using var scope = BeginScopeOnRealStorage(fleet);
        RecurringJobSchedule.Assert(new AutofacServiceProvider(scope));

        var result = await Check(scope, fleet).CheckHealthAsync(new HealthCheckContext(), CancellationToken.None);

        result.Status.ShouldBe(HealthStatus.Healthy, result.Description);
        result.Data["registered"].ShouldBe(true);
        result.Data["cron"].ShouldBe("*/5 * * * *");
        ((DateTime)result.Data["nextExecutionUtc"]).ShouldBeGreaterThan(DateTime.UtcNow.AddSeconds(-5), "a freshly registered entry is due in the future");
        result.Data["strandedCandidates"].ShouldBe(await scope.Resolve<IStrandedCredentialCensus>().CountAsync(CancellationToken.None), "the backlog is the census over this database");
    }

    [Fact]
    public async Task A_database_no_worker_registered_the_sweep_in_reads_back_as_not_registered()
    {
        // The API pod's view of an API-only fleet, or of workers whose build predates the sweep: Hangfire holds no entry.
        using var fleet = new HangfireFleetHarness(_fixture.ConnectionString);
        using var scope = BeginScopeOnRealStorage(fleet);

        var result = await Check(scope, fleet).CheckHealthAsync(new HealthCheckContext(), CancellationToken.None);

        result.Status.ShouldBe(HealthStatus.Degraded);
        result.Description.ShouldNotBeNull().ShouldContain("not registered");
        result.Data["registered"].ShouldBe(false);
    }

    [Fact]
    public async Task An_entry_a_previous_version_scheduler_disabled_reads_back_as_disabled_without_the_error_text_it_left()
    {
        using var fleet = new HangfireFleetHarness(_fixture.ConnectionString);
        using var scope = BeginScopeOnRealStorage(fleet);
        RecurringJobSchedule.Assert(new AutofacServiceProvider(scope));

        // Field for field what RecurringJobScheduler leaves when it cannot load a job type (RecurringJobScheduleKeeperFlowTests).
        Write(fleet, new Dictionary<string, string> { ["NextExecution"] = string.Empty, ["Error"] = "Could not load the job: no such type", ["RetryAttempt"] = "5" });

        var result = await Check(scope, fleet).CheckHealthAsync(new HealthCheckContext(), CancellationToken.None);

        result.Status.ShouldBe(HealthStatus.Degraded);
        result.Description.ShouldNotBeNull().ShouldContain("disabled");
        result.Description.ShouldContain("recorded an error");
        result.Description.ShouldNotContain("Could not load the job", customMessage: "the stored error is an exception chain that can name the storage host; the report never repeats it");
    }

    [Fact]
    public async Task A_schedule_whose_next_run_slipped_far_into_the_past_reads_back_as_overdue()
    {
        using var fleet = new HangfireFleetHarness(_fixture.ConnectionString);
        using var scope = BeginScopeOnRealStorage(fleet);
        RecurringJobSchedule.Assert(new AutofacServiceProvider(scope));

        Write(fleet, new Dictionary<string, string> { ["NextExecution"] = JobHelper.SerializeDateTime(DateTime.UtcNow.AddMinutes(-30)) });

        var result = await Check(scope, fleet).CheckHealthAsync(new HealthCheckContext(), CancellationToken.None);

        result.Status.ShouldBe(HealthStatus.Degraded);
        result.Description.ShouldNotBeNull().ShouldContain("overdue");
    }

    private static CredentialRepairSweepHealthCheck Check(ILifetimeScope scope, HangfireFleetHarness fleet) => new(new ChildScopeFactory(scope), TimeProvider.System, NullLogger<CredentialRepairSweepHealthCheck>.Instance, fleet.Storage);

    /// <summary>Fields of the sweep's recurring entry, written with Hangfire's own write transaction.</summary>
    private static void Write(HangfireFleetHarness fleet, Dictionary<string, string> fields)
    {
        using var connection = fleet.Storage.GetConnection();
        using var transaction = connection.CreateWriteTransaction();

        transaction.SetRangeInHash($"recurring-job:{CredentialRepairSweepHealthCheck.JobId}", fields);
        transaction.Commit();
    }

    private ILifetimeScope BeginScopeOnRealStorage(HangfireFleetHarness fleet) =>
        fleet.BeginScope(_fixture, builder => builder.RegisterInstance(new CodeSpaceBackgroundJobClient(() => new BackgroundJobClient(fleet.Storage), () => new RecurringJobManager(fleet.Storage))).As<ICodeSpaceBackgroundJobClient>());

    private sealed class ChildScopeFactory(ILifetimeScope parent) : IServiceScopeFactory
    {
        public IServiceScope CreateScope() => new ChildScope(parent.BeginLifetimeScope());
    }

    private sealed class ChildScope(ILifetimeScope scope) : IServiceScope
    {
        public IServiceProvider ServiceProvider { get; } = new AutofacServiceProvider(scope);

        public void Dispose() => scope.Dispose();
    }
}
