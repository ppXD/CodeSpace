using CodeSpace.Core.DependencyInjection;

namespace CodeSpace.Core.Services.Agents.Context;

/// <summary>
/// Default <see cref="IContextSourceRegistry"/> — indexes every registered <see cref="IContextSource"/> by its
/// <see cref="IContextSource.Kind"/>. Mirrors <c>TaskLaunchSeedProviderRegistry</c> / <c>AgentHarnessRegistry</c>
/// EXACTLY: DI injects all sources, this dedups (a duplicate kind throws in the ctor) + resolves (an unknown kind
/// throws; <see cref="TryResolve"/> returns false). SCOPED (not singleton) because the sources read per-request DB
/// state — it is resolved inside the <c>get_context</c> tool's per-call scope so each pull gets a fresh DbContext
/// (no cross-connection sharing). Adding a source needs no wiring here — the marker auto-registers it.
/// </summary>
public sealed class ContextSourceRegistry : IContextSourceRegistry, IScopedDependency
{
    private readonly IReadOnlyDictionary<string, IContextSource> _byKind;

    public ContextSourceRegistry(IEnumerable<IContextSource> sources)
    {
        var list = sources.ToList();

        var duplicates = list.GroupBy(s => s.Kind).Where(g => g.Count() > 1).Select(g => g.Key).ToList();

        if (duplicates.Count > 0)
            throw new InvalidOperationException($"Duplicate IContextSource kinds: {string.Join(", ", duplicates)}");

        _byKind = list.ToDictionary(s => s.Kind, StringComparer.Ordinal);
        All = list.OrderBy(s => s.Kind, StringComparer.Ordinal).ToList();
    }

    public IReadOnlyList<IContextSource> All { get; }

    public IContextSource Resolve(string kind)
    {
        if (!_byKind.TryGetValue(kind, out var source))
            throw new InvalidOperationException($"No IContextSource registered for kind '{kind}'. Drop a Context/Sources/ impl that self-registers.");

        return source;
    }

    public bool TryResolve(string kind, out IContextSource source) => _byKind.TryGetValue(kind, out source!);
}
