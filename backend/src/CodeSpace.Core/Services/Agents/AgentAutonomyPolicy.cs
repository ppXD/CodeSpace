using CodeSpace.Messages.Agents;

namespace CodeSpace.Core.Services.Agents;

/// <summary>
/// The single source of truth for "autonomy tier → concrete sandbox knobs" — a pure, total projection of
/// <see cref="AgentAutonomyLevel"/> onto BOTH the <see cref="AgentPermissions"/> a harness enforces
/// (<see cref="Derive"/>) and the <see cref="AgentResourceCeilings"/> the sandbox runner's cgroup enforces
/// (<see cref="Ceilings"/>).
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

    /// <summary>
    /// The tier's per-run resource ceilings — the memory + cpu caps the durable launch turns into this run's cgroup-v2
    /// <c>memory.max</c> / <c>cpu.max</c>. COMMITTED VALUES, changed by PR: they are not read from configuration, and
    /// there is no switch that disables them, because a run allowed unlimited memory is exactly the state this table
    /// ends. <paramref name="hostMemoryBudgetMb"/> is the operator's per-run host budget (see
    /// <c>RuntimeSettings.AgentMemoryCeilingMb</c>) and can only NARROW the memory row — never raise it, never reach
    /// zero.
    ///
    /// <para><b>Why these numbers.</b> Each row is the largest working set the tier's OWN capabilities (see
    /// <see cref="Derive"/>) can legitimately produce; above that is a runaway, not work. Confined may not write and
    /// has no network, so it cannot compile or install — it holds the CLI plus what it reads. Standard may write its
    /// workspace, so it compiles and tests what is already vendored (a mid-size solution build or a bundler peaks
    /// around 2 GiB). Trusted adds network, so it also RESOLVES dependencies, the hungriest phase. Unleashed differs
    /// from Trusted in no concrete knob this policy sets today, so it deliberately repeats Trusted's row rather than
    /// inventing headroom the permission table does not grant.</para>
    ///
    /// <para><b>What these do NOT promise.</b> They bound ONE run, not the host: nothing caps how many agent runs a
    /// worker hosts at once (the engine's frontier default alone is 8), so the sum of live ceilings can exceed host
    /// memory. What they buy is that an overrun is charged to, and killed inside, the run that caused it — attributable
    /// and survivable — instead of the kernel picking a victim and taking the worker down with every sibling run on it.
    /// An operator whose host cannot afford a row narrows it with the host budget.</para>
    /// </summary>
    public static AgentResourceCeilings Ceilings(AgentAutonomyLevel level, int? hostMemoryBudgetMb) => NarrowToHostBudget(Committed(level), hostMemoryBudgetMb);

    /// <summary>The committed tier table. An UNKNOWN tier falls back to the MOST restrictive row (Confined's), never to unlimited — a value this policy cannot recognise must not widen what a run may take.</summary>
    private static AgentResourceCeilings Committed(AgentAutonomyLevel level) => level switch
    {
        AgentAutonomyLevel.Confined  => new AgentResourceCeilings { MemoryMb = 1024, CpuPercent = 100 },
        AgentAutonomyLevel.Standard  => new AgentResourceCeilings { MemoryMb = 4096, CpuPercent = 400 },
        AgentAutonomyLevel.Trusted   => new AgentResourceCeilings { MemoryMb = 6144, CpuPercent = 400 },
        AgentAutonomyLevel.Unleashed => new AgentResourceCeilings { MemoryMb = 6144, CpuPercent = 400 },
        _ => new AgentResourceCeilings { MemoryMb = 1024, CpuPercent = 100 },
    };

    /// <summary>
    /// Apply the operator's per-run host memory budget: it wins only when it is POSITIVE and LOWER than the tier's
    /// committed row. Narrow-only by construction, so the knob is a value for a smaller host and can never become the
    /// off switch a zero (= unlimited) would be. There is no cpu twin: a cpu quota overrun throttles, so overcommitting
    /// cpu degrades a host rather than killing it, and no operator needs to shrink it to keep the worker alive.
    /// </summary>
    private static AgentResourceCeilings NarrowToHostBudget(AgentResourceCeilings committed, int? hostMemoryBudgetMb) =>
        hostMemoryBudgetMb is { } budget && budget > 0 && budget < committed.MemoryMb ? committed with { MemoryMb = budget } : committed;

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
