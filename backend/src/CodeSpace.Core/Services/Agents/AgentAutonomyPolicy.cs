using CodeSpace.Core.Settings;
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
    /// The ceiling a deployment that has NOT configured one gets: the TOP tier, i.e. no deployment bound beyond the
    /// per-route ones that already exist. It is deliberately the highest tier rather than a safer-looking
    /// <see cref="AgentAutonomyLevel.Trusted"/>: Trusted and Unleashed grant identical sandbox knobs
    /// (<see cref="Derive"/>, <see cref="Ceilings"/>) but NOT identical governance — <c>AgentToolGate</c> lets only
    /// Unleashed run a destructive tool unattended — so a Trusted default would silently re-gate every authored
    /// <c>agent.run</c> node pinned to Unleashed. Lowering the ceiling is an operator DECISION, committed as
    /// <c>Sandbox:MaxAutonomy</c> and reviewed as a PR, never a default that arrives with an upgrade.
    /// </summary>
    public const AgentAutonomyLevel DefaultDeploymentCeiling = AgentAutonomyLevel.Unleashed;

    /// <summary>
    /// This DEPLOYMENT's autonomy ceiling (<c>RuntimeSettings.MaxAutonomy</c>) — the tier no run on this host may
    /// exceed, whatever asked for it. The one accessor here that reads configuration rather than taking it as a
    /// parameter, for the same reason <c>BubblewrapSandbox.IsRequired</c> does: its consumers are the router's static
    /// caps merge and the agent.run node's static config read, neither of which has a container to inject into.
    ///
    /// <para>It is a CEILING, so it composes with the others by <see cref="Clamp"/> and can only ever narrow. At its
    /// default (<see cref="DefaultDeploymentCeiling"/>) every clamp is an identity, which is what makes the setting
    /// inert until an operator commits a lower value.</para>
    /// </summary>
    public static AgentAutonomyLevel DeploymentCeiling => Parse(RuntimeSettings.Current.MaxAutonomy, DefaultDeploymentCeiling);

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

    /// <summary>
    /// The one-line EFFECTIVE network posture of a run, for the reader of its journal — the honest answer to "did
    /// this run have the internet, and who decided?". Derived from the same table <see cref="Derive"/> enforces, so
    /// the sentence can never drift from the sandbox: <paramref name="effective"/> is the run's CLAMPED tier (what
    /// the agents actually got) and <paramref name="ceiling"/> the route's bound (what they were ALLOWED to ask
    /// for). Four states, all decision-relevant:
    ///
    /// <list type="bullet">
    ///   <item><c>Network: on (Trusted)</c> — the operator asked, and policy allowed it.</item>
    ///   <item><c>Network: off (Standard) — severed only where the sandbox confines</c> — policy would have allowed
    ///     network; nobody asked. The default.</item>
    ///   <item><c>Network: clamped off by policy (ceiling Standard) — severed only where the sandbox confines</c> —
    ///     the route's ceiling cannot reach a network-granting tier at all, so this run had no network available
    ///     however it was launched. Naming the ceiling is the point: "off" and "off because you could not have it"
    ///     are different facts.</item>
    ///   <item><c>Network: clamped off by deployment ceiling (Standard) — severed only where the sandbox
    ///     confines</c> — the same fact one level up: this HOST's committed <c>Sandbox:MaxAutonomy</c> denies
    ///     network to every tier under it, so no effort tier, no caps override and no authored node could have
    ///     obtained it. Named FIRST when both bind, because it is the only one of the two an operator cannot change
    ///     by relaunching differently.</item>
    /// </list>
    ///
    /// <para><b>Why "off" is qualified — and when it stops being.</b> The tier's <see cref="AgentPermissions.Network"/>
    /// becomes a real severed namespace only where <c>LocalProcessRunner</c> rewrites the command as a bubblewrap
    /// invocation, which needs <c>BubblewrapSandbox.Available</c> — absent on macOS development, on a host without
    /// <c>bwrap</c>, and on one that denies unprivileged user namespaces. <c>Sandbox:RequireConfinement</c> (the
    /// setting that turns an unconfinable host into a refused run) is committed off, so with nothing else to go on an
    /// unqualified "off" would be a claim this sentence cannot make. A run whose launch RECORDED its confinement
    /// (<paramref name="confinement"/>, from <c>agent_run.sandbox_confinement</c>) has something else to go on: the
    /// caveat is then replaced by what actually happened — <c>confined: egress severed</c>, or the loud
    /// <c>OFF REQUESTED BUT UNCONFINED</c> naming the wall the host hit. Passing null (an old run, an un-launched
    /// composer preview) keeps the hedge, which is the honest answer when nothing was recorded. The "on" sentence
    /// takes no qualifier either way: it claims network, which the tier alone already settles.</para>
    /// </summary>
    public static string DescribeNetwork(AgentAutonomyLevel effective, AgentAutonomyLevel ceiling, AgentAutonomyLevel deploymentCeiling, SandboxConfinement? confinement = null)
    {
        // "on" needs no record to be honest — it was never the claim this row exists to qualify — so it keeps its
        // unqualified sentence whether or not a posture was recorded.
        if (Derive(effective).Network == AgentNetworkAccess.On) return $"Network: on ({effective})";

        var qualifier = OffQualifier(confinement);

        if (Derive(deploymentCeiling).Network != AgentNetworkAccess.On) return $"Network: clamped off by deployment ceiling ({deploymentCeiling}){qualifier}";

        if (Derive(ceiling).Network != AgentNetworkAccess.On) return $"Network: clamped off by policy (ceiling {ceiling}){qualifier}";

        return $"Network: off ({effective}){qualifier}";
    }

    /// <summary>
    /// What replaces <see cref="ConfinementCaveat"/> once a run's launch recorded its posture. An unconfined run is
    /// stated LOUDLY (upper case, naming the reason): "off" there is a permission the OS never enforced, which is a
    /// materially different fact from a severed namespace and must not read like a milder version of it.
    /// <see cref="SandboxConfinementOutcome.NotApplicable"/> is such a run too — no confinement was even attempted.
    /// </summary>
    private static string OffQualifier(SandboxConfinement? confinement) => confinement switch
    {
        null => ConfinementCaveat,
        { Outcome: SandboxConfinementOutcome.Confined, NetworkSevered: true } => " — confined: egress severed",
        { Outcome: SandboxConfinementOutcome.Confined } => " — confined, but egress was NOT severed",
        { Outcome: SandboxConfinementOutcome.Unconfined } c => $" — OFF REQUESTED BUT UNCONFINED: this host cannot sever egress ({c.Reason ?? "unknown"})",
        _ => " — OFF REQUESTED BUT UNCONFINED: this runner applies no confinement",
    };

    /// <summary>
    /// The qualifier an "off" posture carries when NO confinement record exists — the sandbox severs egress only
    /// where it actually confines (see <see cref="DescribeNetwork"/>). It remains the Launch composer's wording by
    /// construction: the composer speaks before a run exists, so there is never a record to resolve. A named constant
    /// because the two wordings are pinned against each other by a committed fixture
    /// (<c>frontend/src/lib/networkPosture.fixture.json</c>) that both stacks assert on.
    /// </summary>
    public const string ConfinementCaveat = " — severed only where the sandbox confines";
}
