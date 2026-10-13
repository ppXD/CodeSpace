using CodeSpace.Core.DependencyInjection;
using CodeSpace.Messages.Fleet;
using Hangfire;
using Hangfire.Storage.Monitoring;

namespace CodeSpace.Core.Services.Fleet;

/// <summary>
/// The fleet as Hangfire's server table records it. Every build — this one and every previous one — announces each of
/// its job servers there with the queues it listens on and refreshes a heartbeat, so it is the one registry a
/// previous-version worker writes without knowing this reader exists.
///
/// <para>The storage is optional: a host that registered no Hangfire storage (a test container with no job server)
/// observes no fleet. Every deployed role registers storage — the public pod too, to enqueue.</para>
/// </summary>
public sealed class HangfireWorkerFleetRegistry : IWorkerFleetRegistry, ISingletonDependency
{
    private readonly JobStorage? _storage;

    public HangfireWorkerFleetRegistry(JobStorage? storage = null) => _storage = storage;

    public bool ObservesFleet => _storage is not null;

    public IReadOnlyList<WorkerServerHeartbeat> ReadServers() => _storage is null ? [] : _storage.GetMonitoringApi().Servers().Select(ToHeartbeat).ToList();

    /// <summary>Hangfire stores the heartbeat as a UTC timestamp without a zone, so it comes back unspecified; it is UTC.</summary>
    private static WorkerServerHeartbeat ToHeartbeat(ServerDto server) =>
        new(server.Name, server.Queues?.ToList() ?? [], server.Heartbeat is { } heartbeat ? DateTime.SpecifyKind(heartbeat, DateTimeKind.Utc) : null);
}
