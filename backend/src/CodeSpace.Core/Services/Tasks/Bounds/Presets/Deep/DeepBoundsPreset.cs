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
        MaxRounds = 6,
        MaxTotalSpawns = 20,
        AutonomyCeiling = "Standard",
        RequiresApproval = false,
    };
}
