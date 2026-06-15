namespace CodeSpace.Core.Services.Tasks.Capabilities;

/// <summary>
/// Resolves capability availability by an open capability string — the same shape as
/// <c>IAgentHarnessRegistry</c> / <c>ITaskRecipeRegistry</c>. A new capability becomes answerable purely by
/// registering its <see cref="ICapabilityProbe"/> — no edit here.
/// </summary>
public interface ICapabilityProbeRegistry
{
    /// <summary>Every capability a probe is registered for — the "which capabilities are gated" surface.</summary>
    IReadOnlyList<string> Capabilities { get; }

    /// <summary>
    /// Whether <paramref name="capability"/> is available. When a probe is registered for it, returns the
    /// probe's verdict; when NONE is (an unknown capability), fails OPEN to true — the router degrade is a UX
    /// nicety, not the security boundary, so an unprobed capability never blocks routing (the projection's own
    /// execution-time gate is the real enforcer).
    /// </summary>
    bool IsAvailable(string capability);
}
