using CodeSpace.Core.DependencyInjection;
using CodeSpace.Messages.Tasks;
using CodeSpace.Messages.Tasks.Effort;

namespace CodeSpace.Core.Services.Tasks.Bounds.Presets.Deep;

/// <summary>
/// The <c>deep</c> bounds preset (Rule 18.3 — one impl beside its variant folder) — the most generous tier:
/// wide parallelism, many rounds, a large spawn budget for risky / expensive work. Self-registers via
/// <see cref="ISingletonDependency"/>; the kind string matches <see cref="TaskEffortModes.Deep"/> so the router
/// resolves it by the effort mode. Caps are advisory at L2 (the single-agent builder does not consume them).
/// </summary>
public sealed class DeepBoundsPreset : IBoundsPreset, ISingletonDependency
{
    public string PresetKind => TaskEffortModes.Deep;

    public RouteCaps ToCaps() => new()
    {
        MaxParallelism = 5,
        // A sequential multi-subtask plan needs one spawn round PER subtask (a dependency chain can't parallelize) plus
        // plan + confirm + a closing merge/stop — so 6 could not finish even a 4-subtask chain. 12 gives a real Deep plan
        // room to run to completion with retry/merge slack; MaxTotalSpawns + the cost cap + no-progress still bound runaway.
        MaxRounds = 12,
        MaxTotalSpawns = 20,
        AutonomyCeiling = "Standard",
        RequiresApproval = false,
    };
}
