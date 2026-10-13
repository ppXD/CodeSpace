namespace CodeSpace.Core.Services.Fleet;

/// <summary>
/// Whether a new data shape may be written yet: true only when a worker of the deployment is live and every live one
/// advertises the capability to read it (<see cref="FleetCapabilityMarkers"/>). While a previous-version worker is live
/// — a rolling deploy, or a worker rollback — or no worker is, a writer keeps writing the shape every worker reads.
/// </summary>
public interface IFleetCapabilities
{
    bool AllWorkersSupport(string capability);
}
