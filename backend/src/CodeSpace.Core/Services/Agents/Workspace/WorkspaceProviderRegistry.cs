using CodeSpace.Core.DependencyInjection;

namespace CodeSpace.Core.Services.Agents.Workspace;

/// <summary>
/// Default <see cref="IWorkspaceProviderRegistry"/> — indexes every registered
/// <see cref="IWorkspaceProvider"/> by its <see cref="IWorkspaceProvider.Kind"/>. Mirrors
/// <c>SandboxRunnerRegistry</c>: the DI container injects all providers, this dedups + resolves.
/// Registered automatically via the <see cref="ISingletonDependency"/> marker, so adding a provider
/// needs no wiring here.
/// </summary>
public sealed class WorkspaceProviderRegistry : IWorkspaceProviderRegistry, ISingletonDependency
{
    private readonly IReadOnlyDictionary<string, IWorkspaceProvider> _byKind;

    public WorkspaceProviderRegistry(IEnumerable<IWorkspaceProvider> providers)
    {
        var list = providers.ToList();

        var duplicates = list.GroupBy(p => p.Kind).Where(g => g.Count() > 1).Select(g => g.Key).ToList();

        if (duplicates.Count > 0)
            throw new InvalidOperationException($"Duplicate IWorkspaceProvider kinds: {string.Join(", ", duplicates)}");

        _byKind = list.ToDictionary(p => p.Kind);
        All = list;
    }

    public IReadOnlyList<IWorkspaceProvider> All { get; }

    public IWorkspaceProvider Resolve(string kind)
    {
        if (!_byKind.TryGetValue(kind, out var provider))
            throw new InvalidOperationException($"No IWorkspaceProvider registered for kind '{kind}'. Ensure the corresponding provider is loaded.");

        return provider;
    }
}
