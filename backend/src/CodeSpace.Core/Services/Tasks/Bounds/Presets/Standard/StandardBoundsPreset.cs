using CodeSpace.Core.DependencyInjection;
using CodeSpace.Messages.Tasks;
using CodeSpace.Messages.Tasks.Effort;

namespace CodeSpace.Core.Services.Tasks.Bounds.Presets.Standard;

/// <summary>
/// The <c>standard</c> bounds preset (Rule 18.3 — one impl beside its variant folder) — the moderate default
/// tier: a few parallel branches. Self-registers via <see cref="ISingletonDependency"/>; the kind string matches
/// <see cref="TaskEffortModes.Standard"/> so the router resolves it by the effort mode. Caps are advisory at L2
/// (the single-agent builder does not consume them). The tier tunes ONLY agent CONCURRENCY — a supervised run
/// loops until done, bounded by the model's success stop / the cost cap / no-progress, never a round count (see
/// <c>DeepBoundsPreset</c> for the rationale).
///
/// <para><b>Why the ceiling is Trusted.</b> The ceiling is a BOUND on what the operator may ask for, not the
/// posture a run gets: every recipe recommends <c>Standard</c> and the composer defaults to it, so an unasked
/// launch still runs with network OFF. Holding the ceiling at Standard instead made the network choice
/// unaskable — a Trusted request was silently reduced, so the composer could only ever offer no-network work.
/// Trusted is the FIRST tier <c>AgentAutonomyPolicy.Derive</c> gives <c>AgentNetworkAccess.On</c>; Unleashed
/// stays out of reach on every preset. The bound still binds downstream: the supervisor clamps each spawn to the
/// run's OWN tier (<c>RealSupervisorActionExecutor.ClampAutonomy</c>), so a Standard run's agents cannot reach
/// network however the model asks.</para>
/// </summary>
public sealed class StandardBoundsPreset : IBoundsPreset, ISingletonDependency
{
    public string PresetKind => TaskEffortModes.Standard;

    public RouteCaps ToCaps() => new()
    {
        MaxParallelism = 3,
        AutonomyCeiling = "Trusted",
        RequiresApproval = false,
    };
}
