using CodeSpace.Api.Extensions.Hangfire;
using CodeSpace.Core.Constants;
using CodeSpace.Core.Jobs.RecurringJobs;
using CodeSpace.Core.Services.Fleet;
using CodeSpace.E2ETests.Infrastructure;
using Hangfire;
using Hangfire.PostgreSql;
using Hangfire.PostgreSql.Factories;
using Hangfire.Server;
using Hangfire.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Serilog.Core;
using Serilog.Events;
using Shouldly;

namespace CodeSpace.E2ETests.Jobs;

/// <summary>
/// What a rolling worker deploy depends on, observed on the real worker host: every job server it starts advertises the
/// fleet capability markers in Hangfire's server table, its fleet gate reads that table and says yes for an all-current
/// fleet and no the moment a previous-version worker's servers sit beside it or once no server is left, and its
/// schedule keeper reschedules the recurring entries a previous-version scheduler disabled.
///
/// <para>Tier: 🟢 High-fidelity (Rule 12) — the production <c>WorkerHangfireRegistrar</c> starts two real
/// <c>BackgroundJobServer</c>s against a throw-away Postgres database; the gate is the host's own
/// <c>HangfireWorkerFleetRegistry</c> over the host's own <c>JobStorage</c>; the previous-version servers are
/// announced through Hangfire's own <c>AnnounceServer</c> with the queues the previous build's registrar gave them;
/// the disabled entries are written with Hangfire's own write transaction, field for field what its scheduler leaves.
/// The keeper's pass is invoked directly rather than waiting out its five-minute interval.</para>
/// </summary>
[Trait("Category", "E2E")]
[Trait("Surface", "Worker")]
public sealed class WorkerFleetCapabilityE2ETests
{
    [Fact]
    public async Task The_worker_host_advertises_its_capabilities_and_its_gate_sees_a_previous_version_worker_join()
    {
        await using var factory = new RecurringJobWorkerHostFactory(new DiscardingSink());
        await factory.InitializeAsync();
        _ = factory.Services;   // builds and starts the host, which starts both job servers

        var storage = OpenStorageOnTheFixtureDatabase(factory);
        var servers = await WaitForServersAsync(storage, expected: 2);

        foreach (var server in servers)
            FleetCapabilityMarkers.Advertised.Except(server.Queues).ShouldBeEmpty($"server {server.Name} must advertise every capability marker, or its peers take it for a previous-version worker and never seal");

        FreshGate(factory).AllWorkersSupport(FleetCapabilityMarkers.SealedPayloadV1).ShouldBeTrue("an all-current fleet reads sealed payloads");

        AnnouncePreviousVersionWorker(storage);

        FreshGate(factory).AllWorkersSupport(FleetCapabilityMarkers.SealedPayloadV1).ShouldBeFalse("a live previous-version worker reads only the public column, so sealing must wait for it to go");
    }

    [Fact]
    public async Task Once_the_worker_hosts_servers_stop_the_gate_holds_sealing_back_until_a_worker_is_live_again()
    {
        // A worker rollback's gap: every current worker stopped, the previous ones not started. The API pod still forks
        // runs through the gate, and the next worker to join may be a previous-version one.
        await using var factory = new RecurringJobWorkerHostFactory(new DiscardingSink());
        await factory.InitializeAsync();
        _ = factory.Services;

        var storage = OpenStorageOnTheFixtureDatabase(factory);
        await WaitForServersAsync(storage, expected: 2);
        GateOver(storage).AllWorkersSupport(FleetCapabilityMarkers.SealedPayloadV1).ShouldBeTrue("precondition: the host's own servers are live and current");

        await factory.StartedHost.ShouldNotBeNull().StopAsync(TimeSpan.FromSeconds(30));   // a worker pod's graceful stop
        await WaitForServersGoneAsync(storage);

        GateOver(storage).AllWorkersSupport(FleetCapabilityMarkers.SealedPayloadV1).ShouldBeFalse("with no live worker the gate cannot vouch for the next one to join, so a fork staged now must be the shape every worker reads");
    }

    [Fact]
    public async Task The_worker_hosts_keeper_reschedules_entries_a_previous_version_scheduler_disabled()
    {
        await using var factory = new RecurringJobWorkerHostFactory(new DiscardingSink());
        await factory.InitializeAsync();
        var keeper = factory.Services.GetServices<IHostedService>().OfType<RecurringJobScheduleKeeper>().ShouldHaveSingleItem("the Worker role must run exactly one schedule keeper");

        var storage = OpenStorageOnTheFixtureDatabase(factory);
        var disabled = new[] { nameof(DepartedMemberCredentialRetirementRecurringJob), nameof(DisconnectedCredentialBindingRepairRecurringJob), nameof(WebhookClaimPurgeRecurringJob) };
        DisableAsAPreviousVersionSchedulerDoes(storage, disabled);

        keeper.Reassert();

        foreach (var entry in Entries(storage).Where(entry => disabled.Contains(entry.Id)))
            entry.NextExecution.ShouldNotBeNull($"{entry.Id} stays off forever unless something reschedules it — no current worker boots again after a rolling deploy finishes. Check RecurringJobScheduleKeeper.Reassert and that AddOrUpdate still reschedules an entry carrying an Error.");
    }

    private static IFleetCapabilities FreshGate(RecurringJobWorkerHostFactory factory) =>
        new FleetCapabilities(factory.Services.GetRequiredService<IWorkerFleetRegistry>(), TimeProvider.System, NullLogger<FleetCapabilities>.Instance);

    private static IFleetCapabilities GateOver(JobStorage storage) => new FleetCapabilities(new HangfireWorkerFleetRegistry(storage), TimeProvider.System, NullLogger<FleetCapabilities>.Instance);

    private static JobStorage OpenStorageOnTheFixtureDatabase(RecurringJobWorkerHostFactory factory)
    {
        var options = HangfireRegistrarBase.BuildStorageOptions();
        return new PostgreSqlStorage(new NpgsqlConnectionFactory(factory.ConnectionString, options), options);
    }

    /// <summary>Hangfire announces a server shortly after the host starts; the predicate starts false, so an empty read can never pass.</summary>
    private static async Task<IReadOnlyList<Hangfire.Storage.Monitoring.ServerDto>> WaitForServersAsync(JobStorage storage, int expected)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        IReadOnlyList<Hangfire.Storage.Monitoring.ServerDto> servers = [];

        while (servers.Count < expected && DateTime.UtcNow < deadline)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(250));
            servers = storage.GetMonitoringApi().Servers().ToList();
        }

        servers.Count.ShouldBe(expected, $"the worker host must start its control and agent servers within 30s; check WorkerHangfireRegistrar.RegisterHangfire and `select id, data from hangfire.server` on the test database");

        return servers;
    }

    /// <summary>A gracefully stopped server removes its own row; the predicate reads first, so a table that never held the servers cannot pass for one they left.</summary>
    private static async Task WaitForServersGoneAsync(JobStorage storage)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        var servers = storage.GetMonitoringApi().Servers().Count;

        while (servers > 0 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(250));
            servers = storage.GetMonitoringApi().Servers().Count;
        }

        servers.ShouldBe(0, "a stopped BackgroundJobServer must remove its row from hangfire.server within 30s; check `select id, lastheartbeat from hangfire.server` on the test database");
    }

    private static void AnnouncePreviousVersionWorker(JobStorage storage)
    {
        using var connection = storage.GetConnection();
        connection.AnnounceServer($"codespace-control-previous:{Environment.ProcessId}:{Guid.NewGuid():N}", new ServerContext { Queues = [HangfireConstants.DefaultQueue], WorkerCount = 4 });
        connection.AnnounceServer($"codespace-agents-previous:{Environment.ProcessId}:{Guid.NewGuid():N}", new ServerContext { Queues = [HangfireConstants.AgentQueue], WorkerCount = 4 });
    }

    private static void DisableAsAPreviousVersionSchedulerDoes(JobStorage storage, IEnumerable<string> recurringJobIds)
    {
        using var connection = storage.GetConnection();
        using var transaction = connection.CreateWriteTransaction();

        foreach (var id in recurringJobIds)
        {
            transaction.SetRangeInHash($"recurring-job:{id}", new Dictionary<string, string> { ["NextExecution"] = string.Empty, ["Error"] = "Hangfire.Common.JobLoadException: Could not load the job.", ["RetryAttempt"] = "5" });
            transaction.AddToSet("recurring-jobs", id, -1.0);
        }

        transaction.Commit();
    }

    private static IReadOnlyList<RecurringJobDto> Entries(JobStorage storage)
    {
        using var connection = storage.GetConnection();
        return connection.GetRecurringJobs();
    }

    private sealed class DiscardingSink : ILogEventSink
    {
        public void Emit(LogEvent logEvent) { }
    }
}
