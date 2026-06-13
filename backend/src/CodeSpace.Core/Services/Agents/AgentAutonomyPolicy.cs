using CodeSpace.Messages.Agents;

namespace CodeSpace.Core.Services.Agents;

/// <summary>
/// The single source of truth for "autonomy tier → concrete sandbox knobs" — a pure, total projection of
/// <see cref="AgentAutonomyLevel"/> onto the <see cref="AgentPermissions"/> a harness enforces.
///
/// Centralizing it here (rather than scattering network/read-only toggles across nodes) is what lets ONE named
/// dial drive the run's posture, and it is the seam future governance knobs (network allowlist, side-effect
/// approval, privileged runner) extend without touching call sites. The mapping table is pinned by a unit test
/// so any change to it is a visible, reviewed decision (Rule 8 spirit). Callers may layer explicit per-field
/// overrides on top of the derived baseline.
/// </summary>
public static class AgentAutonomyPolicy
{
    /// <summary>Derives the baseline <see cref="AgentPermissions"/> for a tier. Unknown values fall back to the safe default.</summary>
    public static AgentPermissions Derive(AgentAutonomyLevel level) => level switch
    {
        AgentAutonomyLevel.Confined  => new AgentPermissions { Network = AgentNetworkAccess.Off, WriteScope = AgentWriteScope.ReadOnly },
        AgentAutonomyLevel.Standard  => new AgentPermissions { Network = AgentNetworkAccess.Off, WriteScope = AgentWriteScope.Workspace },
        AgentAutonomyLevel.Trusted   => new AgentPermissions { Network = AgentNetworkAccess.On,  WriteScope = AgentWriteScope.Workspace },
        AgentAutonomyLevel.Unleashed => new AgentPermissions { Network = AgentNetworkAccess.On,  WriteScope = AgentWriteScope.Workspace },
        _ => new AgentPermissions(),
    };
}
