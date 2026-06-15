using CodeSpace.Core.DependencyInjection;

namespace CodeSpace.Core.Services.Tasks.Capabilities;

/// <summary>
/// Reports whether ONE deployment capability is available right now (Rule 7 — a NARROW sibling capability the
/// router consults, never a wider interface). A capability is an open string (e.g. <c>"supervisor-lane"</c>) a
/// recipe's projection DECLARES it needs at execution via <c>ITaskRecipe.RequiresCapability</c>; the router
/// degrades to the recipe's fallback when the probe says it is unavailable. Self-registers via the
/// <see cref="ISingletonDependency"/> marker (impls live in <c>Capabilities/Probes/&lt;Name&gt;/</c>, Rule 18.3),
/// so a new capability is a sibling probe folder with ZERO edit to the registry / router.
/// </summary>
public interface ICapabilityProbe
{
    /// <summary>The open capability string this probe answers for — the value a recipe names in <c>RequiresCapability</c> + the registry indexes it by. Mirrors <c>IAgentHarness.Kind</c>.</summary>
    string Capability { get; }

    /// <summary>True when the capability is available in this deployment right now (e.g. the feature flag is on). A pure read — no side effects.</summary>
    bool IsAvailable();
}
