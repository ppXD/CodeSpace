using CodeSpace.Core.DependencyInjection;
using CodeSpace.Messages.Tasks;

namespace CodeSpace.Core.Services.Tasks.Bounds;

/// <summary>
/// One named bounds preset — the polymorphic seam the router resolves a tier's safety caps from (Rule 18.3,
/// each impl beside its variant folder under <c>Bounds/Presets/&lt;Kind&gt;/</c>). Each preset owns ONE
/// <see cref="PresetKind"/> (an open string, by the effort-mode ≡ preset-kind convention the router uses to
/// resolve a preset BY the effort mode) and produces a <see cref="RouteCaps"/>. Self-registers via the
/// <see cref="ISingletonDependency"/> marker, so a new preset is a sibling folder with ZERO edit to the registry
/// / router (Rule 7 — a new tier is a new preset, never a wider interface).
///
/// <para>The caps are ADVISORY at L2: the single-agent builder does not consume them yet (it emits a fixed
/// three-node graph with no fan-out / loop). The supervisor / map builders (later PRs) read them, and the
/// <c>RouteCaps</c>→<c>SupervisorGoalConfig</c> fold + the lane clamp stay where the safety clamp already lives
/// at execution — this layer only SELECTS the advisory numbers.</para>
/// </summary>
public interface IBoundsPreset
{
    /// <summary>The preset kind this impl handles — the open string the registry indexes + resolves it by. Mirrors <c>IAgentHarness.Kind</c>.</summary>
    string PresetKind { get; }

    /// <summary>The safety caps this preset imposes — a pure value (same preset → same caps).</summary>
    RouteCaps ToCaps();
}
