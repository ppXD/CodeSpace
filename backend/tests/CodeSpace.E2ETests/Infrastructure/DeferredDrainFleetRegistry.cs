using CodeSpace.Core.Constants;
using CodeSpace.Core.Services.Fleet;
using CodeSpace.Messages.Fleet;

namespace CodeSpace.E2ETests.Infrastructure;

/// <summary>
/// The worker fleet of a host whose jobs the TEST drains (<see cref="DeferredJobClient"/>): that drain is its one
/// worker, it runs this build, and it is live for as long as the host is. The host itself takes the Api role and starts
/// no job server, so Hangfire's server table is empty, and the production fleet gate reads an empty fleet as "the next
/// worker may be a previous-version one" — right for an API pod between worker generations, wrong for a host that runs
/// every job it enqueues. Only the registry is replaced; the gate itself (<see cref="FleetCapabilities"/>) is the
/// production one.
/// </summary>
public sealed class DeferredDrainFleetRegistry : IWorkerFleetRegistry
{
    public bool ObservesFleet => true;

    /// <summary>One server with no recorded heartbeat — the rules take it as alive — listening where the drain runs jobs, with every marker this build advertises.</summary>
    public IReadOnlyList<WorkerServerHeartbeat> ReadServers() => [new("deferred-job-drain", [HangfireConstants.DefaultQueue, HangfireConstants.AgentQueue, .. FleetCapabilityMarkers.Advertised], null)];
}
