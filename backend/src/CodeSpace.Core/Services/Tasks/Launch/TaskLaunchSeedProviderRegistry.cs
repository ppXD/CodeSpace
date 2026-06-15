using CodeSpace.Core.DependencyInjection;

namespace CodeSpace.Core.Services.Tasks.Launch;

/// <summary>
/// Default <see cref="ITaskLaunchSeedProviderRegistry"/> — indexes every registered
/// <see cref="ITaskLaunchSeedProvider"/> by its <see cref="ITaskLaunchSeedProvider.SurfaceKind"/>. Mirrors
/// <c>AgentHarnessRegistry</c> / <c>TaskProjectionRegistry</c> EXACTLY: DI injects all providers, this dedups (a
/// duplicate surface throws in the ctor) + resolves (an unknown surface throws; <see cref="TryResolve"/> returns
/// false). Registered automatically via the <see cref="ISingletonDependency"/> marker, so adding a provider needs
/// no wiring here — the dispatch is <c>Resolve(openString)</c> with zero per-surface switch.
/// </summary>
public sealed class TaskLaunchSeedProviderRegistry : ITaskLaunchSeedProviderRegistry, ISingletonDependency
{
    private readonly IReadOnlyDictionary<string, ITaskLaunchSeedProvider> _bySurface;

    public TaskLaunchSeedProviderRegistry(IEnumerable<ITaskLaunchSeedProvider> providers)
    {
        var list = providers.ToList();

        var duplicates = list.GroupBy(p => p.SurfaceKind).Where(g => g.Count() > 1).Select(g => g.Key).ToList();

        if (duplicates.Count > 0)
            throw new InvalidOperationException($"Duplicate ITaskLaunchSeedProvider surface kinds: {string.Join(", ", duplicates)}");

        _bySurface = list.ToDictionary(p => p.SurfaceKind);
        All = list;
    }

    public IReadOnlyList<ITaskLaunchSeedProvider> All { get; }

    public ITaskLaunchSeedProvider Resolve(string surfaceKind)
    {
        if (!_bySurface.TryGetValue(surfaceKind, out var provider))
            throw new InvalidOperationException($"No ITaskLaunchSeedProvider registered for surface kind '{surfaceKind}'. Drop a Launch/Providers/<Surface>/ impl that self-registers.");

        return provider;
    }

    public bool TryResolve(string surfaceKind, out ITaskLaunchSeedProvider provider) =>
        _bySurface.TryGetValue(surfaceKind, out provider!);
}
