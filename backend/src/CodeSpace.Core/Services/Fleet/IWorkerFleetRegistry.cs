using CodeSpace.Messages.Fleet;

namespace CodeSpace.Core.Services.Fleet;

/// <summary>The registry every job server of the deployment heartbeats into, read as it stands — live and stale servers alike; liveness is <see cref="FleetCapabilityRules"/>' call.</summary>
public interface IWorkerFleetRegistry
{
    /// <summary>False on a host with no registry at all (a test host with no job storage): it runs no job and shares its rows with no worker, so it has no fleet to hold a shape back for.</summary>
    bool ObservesFleet { get; }

    IReadOnlyList<WorkerServerHeartbeat> ReadServers();
}
