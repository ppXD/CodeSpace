using CodeSpace.Core.DependencyInjection;
using CodeSpace.Messages.Tasks;
using CodeSpace.Messages.Tasks.Effort;

namespace CodeSpace.Core.Services.Tasks.Bounds.Presets.Quick;

/// <summary>
/// The <c>quick</c> bounds preset (Rule 18.3 — one impl beside its variant folder) — the tightest tier: a
/// single pass, no fan-out, no spawns. Self-registers via <see cref="ISingletonDependency"/>; the kind string
/// matches <see cref="TaskEffortModes.Quick"/> so the router resolves it by the effort mode (the effort-mode ≡
/// preset-kind convention). Caps are advisory at L2 (the single-agent builder does not consume them).
/// </summary>
public sealed class QuickBoundsPreset : IBoundsPreset, ISingletonDependency
{
    public string PresetKind => TaskEffortModes.Quick;

    public RouteCaps ToCaps() => new()
    {
        MaxParallelism = 1,
        AutonomyCeiling = "Standard",
        RequiresApproval = false,
    };
}
