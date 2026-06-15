using CodeSpace.Core.DependencyInjection;
using CodeSpace.Messages.Tasks;
using CodeSpace.Messages.Tasks.Effort;

namespace CodeSpace.Core.Services.Tasks.Bounds.Presets.Standard;

/// <summary>
/// The <c>standard</c> bounds preset (Rule 18.3 — one impl beside its variant folder) — the moderate default
/// tier: a few parallel branches, a few rounds, a modest spawn budget. Self-registers via
/// <see cref="ISingletonDependency"/>; the kind string matches <see cref="TaskEffortModes.Standard"/> so the
/// router resolves it by the effort mode. Caps are advisory at L2 (the single-agent builder does not consume
/// them).
/// </summary>
public sealed class StandardBoundsPreset : IBoundsPreset, ISingletonDependency
{
    public string PresetKind => TaskEffortModes.Standard;

    public RouteCaps ToCaps() => new()
    {
        MaxParallelism = 3,
        MaxRounds = 3,
        MaxTotalSpawns = 8,
        AutonomyCeiling = "Standard",
        RequiresApproval = false,
    };
}
