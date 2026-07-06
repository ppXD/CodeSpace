using CodeSpace.Core.DependencyInjection;
using CodeSpace.Messages.Tasks;
using CodeSpace.Messages.Tasks.Effort;

namespace CodeSpace.Core.Services.Tasks.Bounds.Presets.Deep;

/// <summary>
/// The <c>deep</c> bounds preset (Rule 18.3 — one impl beside its variant folder) — the most generous tier:
/// wide parallelism for risky / expensive work. Self-registers via <see cref="ISingletonDependency"/>; the kind
/// string matches <see cref="TaskEffortModes.Deep"/> so the router resolves it by the effort mode. Caps are advisory
/// at L2 (the single-agent builder does not consume them).
///
/// <para>The tier tunes ONLY the agent CONCURRENCY (<see cref="RouteCaps.MaxParallelism"/> — how many agents run at
/// once). A supervised run LOOPS UNTIL DONE: it terminates on the model's own success stop, the operator's cost cap
/// (realized token spend), or the best-effort no-progress guard — NOT an arbitrary round or total-spawn count (those
/// strangled real multi-subtask plans). The round / total-spawn ceilings survive ONLY as hidden runaway back-stops
/// (their defaults in <c>SupervisorGoalPlan</c>), never a tuned tier knob.</para>
/// </summary>
public sealed class DeepBoundsPreset : IBoundsPreset, ISingletonDependency
{
    public string PresetKind => TaskEffortModes.Deep;

    public RouteCaps ToCaps() => new()
    {
        MaxParallelism = 5,
        AutonomyCeiling = "Standard",
        RequiresApproval = false,
    };
}
