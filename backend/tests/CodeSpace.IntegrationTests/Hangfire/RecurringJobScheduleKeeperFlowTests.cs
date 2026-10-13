using Autofac;
using Autofac.Extensions.DependencyInjection;
using CodeSpace.Api.Extensions.Hangfire;
using CodeSpace.Core.Jobs.RecurringJobs;
using CodeSpace.Core.Services.Fleet;
using CodeSpace.Core.Services.Jobs;
using CodeSpace.IntegrationTests.Fleet;
using CodeSpace.IntegrationTests.Infrastructure;
using Hangfire;
using Hangfire.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Shouldly;

namespace CodeSpace.IntegrationTests.Hangfire;

/// <summary>
/// The recurring entries a previous-version worker's scheduler disables during a rolling deploy — it cannot load a job
/// type it never had, retries, then leaves the entry with no next execution — come back on the keeper's next pass, and
/// the worker's advertised queues carry the fleet capability markers. Real Hangfire storage on Postgres (a schema of
/// its own), the production <c>CodeSpaceBackgroundJobClient</c>, the production <c>RecurringJobSchedule</c> over the
/// fixture container's real recurring jobs; the disabled state is written with Hangfire's own write transaction,
/// field for field what <c>RecurringJobScheduler</c> leaves (observed against the previous build's scheduler: Error,
/// RetryAttempt 5, an empty NextExecution and score -1).
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public sealed class RecurringJobScheduleKeeperFlowTests
{
    private const string LoadFailure = "Hangfire.Common.JobLoadException: Could not load the job. See inner exception for the details. ---> Newtonsoft.Json.JsonSerializationException: Error converting value \"CodeSpace.Core.Jobs.RecurringJobs.DepartedMemberCredentialRetirementRecurringJob, CodeSpace.Core\" to type 'System.Type'.";

    private static readonly string[] AddedSinceThePreviousBuild = [nameof(DepartedMemberCredentialRetirementRecurringJob), nameof(DisconnectedCredentialBindingRepairRecurringJob), nameof(WebhookClaimPurgeRecurringJob), nameof(PackCloneUrlBackfillRecurringJob)];

    private readonly PostgresFixture _fixture;

    public RecurringJobScheduleKeeperFlowTests(PostgresFixture fixture) { _fixture = fixture; }

    [Fact]
    public void An_entry_a_previous_version_scheduler_disabled_is_rescheduled_by_the_next_pass()
    {
        using var fleet = new HangfireFleetHarness(_fixture.ConnectionString);
        using var scope = BeginScopeOnRealStorage(fleet);
        RecurringJobSchedule.Assert(new AutofacServiceProvider(scope));

        DisableAsAPreviousVersionSchedulerDoes(fleet.Storage, AddedSinceThePreviousBuild);
        EntriesNamed(fleet.Storage, AddedSinceThePreviousBuild).ShouldAllBe(entry => entry.NextExecution == null, "precondition: the entries are disabled");

        Keeper(scope).Reassert();

        foreach (var entry in EntriesNamed(fleet.Storage, AddedSinceThePreviousBuild))
        {
            entry.NextExecution.ShouldNotBeNull($"{entry.Id} must be scheduled again — a disabled entry never fires, and these are the sweeps that clean up after previous-version pods");
            entry.Error.ShouldBeNullOrEmpty();
            entry.RetryAttempt.ShouldBe(0);
            ScheduleScore(fleet, entry.Id).ShouldBeGreaterThan(0, $"{entry.Id} must be back in the scheduler's due set, not parked at -1");
        }
    }

    [Fact]
    public void A_pass_over_a_healthy_schedule_changes_no_entry()
    {
        using var fleet = new HangfireFleetHarness(_fixture.ConnectionString);
        using var scope = BeginScopeOnRealStorage(fleet);
        RecurringJobSchedule.Assert(new AutofacServiceProvider(scope));
        var before = AllEntries(fleet.Storage).ToDictionary(entry => entry.Id, entry => (entry.NextExecution, entry.Cron, entry.Queue));

        Keeper(scope).Reassert();

        AllEntries(fleet.Storage).ToDictionary(entry => entry.Id, entry => (entry.NextExecution, entry.Cron, entry.Queue)).ShouldBe(before);
    }

    [Fact]
    public void The_pass_interval_is_pinned() =>
        // Each pass takes one Hangfire lock per recurring entry on every worker; five minutes keeps that negligible while
        // a disabled sweep — the departed-member token sweep runs every five minutes itself — misses at most one tick.
        RecurringJobScheduleKeeper.Interval.ShouldBe(TimeSpan.FromMinutes(5));

    [Fact]
    public void Both_worker_pools_advertise_every_capability_marker_after_their_own_queue()
    {
        // A pool without the marker reads to every peer as a previous-version worker, and sealing would never resume.
        WorkerHangfireRegistrar.ControlQueues.ShouldBe([Core.Constants.HangfireConstants.DefaultQueue, .. FleetCapabilityMarkers.Advertised]);
        WorkerHangfireRegistrar.AgentQueues.ShouldBe([Core.Constants.HangfireConstants.AgentQueue, .. FleetCapabilityMarkers.Advertised]);
    }

    /// <summary>The keeper as a worker host builds it, with its scopes opened under the test's storage override — the container's own scope factory would open them from the fixture root, whose job client is the in-memory one.</summary>
    private static RecurringJobScheduleKeeper Keeper(ILifetimeScope scope) => new(new ChildScopeFactory(scope), TimeProvider.System, NullLogger<RecurringJobScheduleKeeper>.Instance);

    private ILifetimeScope BeginScopeOnRealStorage(HangfireFleetHarness fleet) =>
        fleet.BeginScope(_fixture, builder => builder.RegisterInstance(new CodeSpaceBackgroundJobClient(() => new BackgroundJobClient(fleet.Storage), () => new RecurringJobManager(fleet.Storage))).As<ICodeSpaceBackgroundJobClient>());

    /// <summary>The writes <c>RecurringJobScheduler</c> makes when it gives up on an entry it cannot load: <c>Disable(error)</c> through <c>UpdateRecurringJob</c>.</summary>
    private static void DisableAsAPreviousVersionSchedulerDoes(JobStorage storage, IEnumerable<string> recurringJobIds)
    {
        using var connection = storage.GetConnection();
        using var transaction = connection.CreateWriteTransaction();

        foreach (var id in recurringJobIds)
        {
            transaction.SetRangeInHash($"recurring-job:{id}", new Dictionary<string, string> { ["NextExecution"] = string.Empty, ["Error"] = LoadFailure, ["RetryAttempt"] = "5" });
            transaction.AddToSet("recurring-jobs", id, -1.0);
        }

        transaction.Commit();
    }

    private static IReadOnlyList<RecurringJobDto> AllEntries(JobStorage storage)
    {
        using var connection = storage.GetConnection();
        return connection.GetRecurringJobs();
    }

    private static IReadOnlyList<RecurringJobDto> EntriesNamed(JobStorage storage, IReadOnlyCollection<string> ids)
    {
        var entries = AllEntries(storage).Where(entry => ids.Contains(entry.Id)).ToList();
        entries.Count.ShouldBe(ids.Count, "every job added since the previous build must be in this build's schedule");

        return entries;
    }

    private double ScheduleScore(HangfireFleetHarness fleet, string recurringJobId)
    {
        using var connection = new NpgsqlConnection(_fixture.ConnectionString);
        connection.Open();

        using var command = new NpgsqlCommand($"SELECT score FROM \"{fleet.Schema}\".set WHERE key = 'recurring-jobs' AND value = @id", connection);
        command.Parameters.AddWithValue("id", recurringJobId);

        return Convert.ToDouble(command.ExecuteScalar());
    }

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
