using CodeSpace.Core.DependencyInjection;

namespace CodeSpace.Core.Services.Tasks.Bounds;

/// <summary>
/// Default <see cref="IBoundsPresetRegistry"/> — indexes every registered <see cref="IBoundsPreset"/> by its
/// <see cref="IBoundsPreset.PresetKind"/>. Mirrors the <c>AgentHarnessRegistry</c> / <c>TaskProjectionRegistry</c>
/// shape: DI injects all presets via <c>IEnumerable&lt;T&gt;</c>, this dedups (a duplicate kind throws in the
/// ctor) + resolves (an unknown kind throws; <see cref="TryResolve"/> returns false). Unlike
/// <c>EffortClassifierRegistry</c> / <c>TaskRecipeRegistry</c> it intentionally exposes NO <c>Default</c> — the
/// router resolves a preset by the effort mode (the effort-mode ≡ preset-kind convention), never via a fallback
/// default, so a bounds Default would be dead. Registered via the <see cref="ISingletonDependency"/> marker, so
/// adding a preset needs no wiring here — the dispatch is <c>Resolve(openString)</c> with zero per-kind switch.
/// </summary>
public sealed class BoundsPresetRegistry : IBoundsPresetRegistry, ISingletonDependency
{
    private readonly IReadOnlyDictionary<string, IBoundsPreset> _byKind;

    public BoundsPresetRegistry(IEnumerable<IBoundsPreset> presets)
    {
        var list = presets.ToList();

        var duplicates = list.GroupBy(p => p.PresetKind).Where(g => g.Count() > 1).Select(g => g.Key).ToList();

        if (duplicates.Count > 0)
            throw new InvalidOperationException($"Duplicate IBoundsPreset kinds: {string.Join(", ", duplicates)}");

        _byKind = list.ToDictionary(p => p.PresetKind);
        All = list;
    }

    public IReadOnlyList<IBoundsPreset> All { get; }

    public IBoundsPreset Resolve(string presetKind)
    {
        if (!_byKind.TryGetValue(presetKind, out var preset))
            throw new InvalidOperationException($"No IBoundsPreset registered for kind '{presetKind}'. Drop a Bounds/Presets/<Kind>/ impl that self-registers.");

        return preset;
    }

    public bool TryResolve(string presetKind, out IBoundsPreset preset) =>
        _byKind.TryGetValue(presetKind, out preset!);
}
