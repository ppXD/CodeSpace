using Autofac;
using CodeSpace.Api.Extensions.Hangfire;
using CodeSpace.Core.Constants;
using CodeSpace.Core.Services.Fleet;
using CodeSpace.Core.Services.Workflows.Runtime;
using CodeSpace.IntegrationTests.Infrastructure;
using Hangfire;
using Hangfire.PostgreSql;
using Hangfire.PostgreSql.Factories;
using Hangfire.Server;
using Npgsql;

namespace CodeSpace.IntegrationTests.Fleet;

/// <summary>
/// A real Hangfire server table, on the fixture's Postgres in a schema of its own, that a test fills the way job servers
/// fill it in a deployment: <see cref="AnnounceCurrentWorker"/> registers this build's two pools with the exact queue
/// lists <c>WorkerHangfireRegistrar</c> gives them, <see cref="AnnouncePreviousVersionWorker"/> registers the two
/// pools the previous build's registrar started (its own queue only — it never knew a capability marker). Both go
/// through Hangfire's own <c>AnnounceServer</c>, the call a starting <c>BackgroundJobServer</c> makes.
///
/// <para><see cref="BeginScope"/> hands out a fixture scope whose fleet gate, registry and sealer read THIS table
/// through the production classes — fresh instances, so no snapshot is shared with another test or with the
/// fixture's root container (which has no job storage and therefore observes no fleet: its gate holds nothing back).</para>
/// </summary>
internal sealed class HangfireFleetHarness : IDisposable
{
    private readonly string _connectionString;

    public HangfireFleetHarness(string connectionString)
    {
        _connectionString = connectionString;
        Schema = $"hangfire_fleet_{Guid.NewGuid():N}";

        var options = HangfireRegistrarBase.BuildStorageOptions();
        options.SchemaName = Schema;

        Storage = new PostgreSqlStorage(new NpgsqlConnectionFactory(connectionString, options), options);
    }

    public string Schema { get; }

    public JobStorage Storage { get; }

    /// <summary>This build's worker: its control and agent pools, each listing the capability markers.</summary>
    public IReadOnlyList<string> AnnounceCurrentWorker() => [Announce("codespace-control-current", WorkerHangfireRegistrar.ControlQueues), Announce("codespace-agents-current", WorkerHangfireRegistrar.AgentQueues)];

    /// <summary>A previous-version worker: the same two pools on the queues its registrar listed — <c>default</c> and <c>agents</c>, nothing else.</summary>
    public IReadOnlyList<string> AnnouncePreviousVersionWorker() => [Announce("codespace-control-previous", [HangfireConstants.DefaultQueue]), Announce("codespace-agents-previous", [HangfireConstants.AgentQueue])];

    public string Announce(string serverName, string[] queues)
    {
        var serverId = $"{serverName}:{Environment.ProcessId}:{Guid.NewGuid():N}";

        using var connection = Storage.GetConnection();
        connection.AnnounceServer(serverId, new ServerContext { Queues = queues, WorkerCount = 1 });

        return serverId;
    }

    /// <summary>Back-date a server's last heartbeat — a worker that was killed and whose row Hangfire's watchdog has not removed yet.</summary>
    public void AgeHeartbeats(IEnumerable<string> serverIds, TimeSpan age)
    {
        using var connection = new NpgsqlConnection(_connectionString);
        connection.Open();

        using var command = new NpgsqlCommand($"UPDATE \"{Schema}\".server SET lastheartbeat = lastheartbeat - @age WHERE id = ANY(@ids)", connection);
        command.Parameters.AddWithValue("age", age);
        command.Parameters.AddWithValue("ids", serverIds.ToArray());
        command.ExecuteNonQuery();
    }

    /// <summary>A fixture scope whose job storage is this table, with its own fleet gate, registry and sealer so their snapshot is this test's alone.</summary>
    public ILifetimeScope BeginScope(PostgresFixture fixture, Action<ContainerBuilder>? configure = null) => fixture.BeginScope(builder =>
    {
        builder.RegisterInstance(Storage).As<JobStorage>();
        builder.RegisterType<HangfireWorkerFleetRegistry>().AsSelf().As<IWorkerFleetRegistry>().SingleInstance();
        builder.RegisterType<FleetCapabilities>().AsSelf().As<IFleetCapabilities>().SingleInstance();
        builder.RegisterType<WorkflowSensitivePayloadSealer>().AsSelf().SingleInstance();
        configure?.Invoke(builder);
    });

    public void Dispose()
    {
        using var connection = new NpgsqlConnection(_connectionString);
        connection.Open();

        using var command = new NpgsqlCommand($"DROP SCHEMA IF EXISTS \"{Schema}\" CASCADE", connection);
        command.ExecuteNonQuery();
    }
}
