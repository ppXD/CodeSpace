namespace CodeSpace.Core.Services.Tasks.Bounds;

/// <summary>
/// Resolves an <see cref="IBoundsPreset"/> by its <see cref="IBoundsPreset.PresetKind"/> — same shape as
/// <c>IAgentHarnessRegistry</c> / <c>ITaskProjectionRegistry</c>. The router picks the kind (by the
/// effort-mode ≡ preset-kind convention); this only maps a kind → its preset. A new preset becomes resolvable
/// by registering its class — no edit here. <see cref="All"/> is the source the confirm card derives its
/// options from (one option per available preset = available effort tier), so the operator's choices are never
/// a hardcoded list.
/// </summary>
public interface IBoundsPresetRegistry
{
    /// <summary>Every registered bounds preset — the "which effort tiers / preset kinds are available" surface.</summary>
    IReadOnlyList<IBoundsPreset> All { get; }

    /// <summary>Resolve the preset for <paramref name="presetKind"/>. Throws when none is registered for that kind.</summary>
    IBoundsPreset Resolve(string presetKind);

    /// <summary>Try-resolve variant — false (and a null out) when no preset is registered for <paramref name="presetKind"/>, for callers (the router) that fall back rather than throw.</summary>
    bool TryResolve(string presetKind, out IBoundsPreset preset);
}
