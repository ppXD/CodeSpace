using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Services.Supervisor;
using CodeSpace.Messages.Tasks;

namespace CodeSpace.Core.Services.Tasks.Capabilities.Probes.SupervisorLane;

/// <summary>
/// The <see cref="TaskCapabilities.SupervisorLane"/> probe (Rule 18.3 — one impl beside its variant folder):
/// wraps the static <see cref="Core.Services.Supervisor.SupervisorLane.IsEnabled"/> feature gate behind the
/// injectable <see cref="ICapabilityProbe"/> seam, so the router degrades <c>deep</c> away from the supervisor
/// projection when the lane is off — an honest fallback rather than an execution-time failure. Self-registers
/// via <see cref="ISingletonDependency"/>; a new capability is a sibling probe folder, never an edit elsewhere.
/// </summary>
public sealed class SupervisorLaneProbe : ICapabilityProbe, ISingletonDependency
{
    public string Capability => TaskCapabilities.SupervisorLane;

    public bool IsAvailable() => Core.Services.Supervisor.SupervisorLane.IsEnabled();
}
