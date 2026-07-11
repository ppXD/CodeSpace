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
    /// <summary>
    /// Clamps a REQUESTED tier down to a CEILING — the lower (less privileged) of the two. The enum is ascending
    /// capability (Confined &lt; Standard &lt; Trusted &lt; Unleashed), so <see cref="Math.Min(int,int)"/> over the
    /// underlying ints is the clamp: a Quick/Standard route (ceiling Standard) can never run a requested Trusted /
    /// Unleashed. Applying it at the SINGLE choke point that stamps the tier (the task launch's agent-profile build)
    /// is what makes the ceiling un-bypassable — the clamped tier is the one that flows through projection → the node
    /// config → <see cref="Derive"/> → the sandbox runner.
    /// </summary>
    public static AgentAutonomyLevel Clamp(AgentAutonomyLevel requested, AgentAutonomyLevel ceiling) =>
        (AgentAutonomyLevel)Math.Min((int)requested, (int)ceiling);

    /// <summary>Parse an autonomy tier string case-insensitively (mirrors agent.run's ReadAutonomyLevel); null / blank / unrecognised → the supplied fallback. The single tier parser, reused by the launch clamp and the caps-override merge.</summary>
    public static AgentAutonomyLevel Parse(string? value, AgentAutonomyLevel fallback) =>
        Enum.TryParse<AgentAutonomyLevel>(value, ignoreCase: true, out var level) ? level : fallback;

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
