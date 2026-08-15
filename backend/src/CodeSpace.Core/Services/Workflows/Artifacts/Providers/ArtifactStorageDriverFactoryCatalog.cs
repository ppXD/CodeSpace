namespace CodeSpace.Core.Services.Workflows.Artifacts.Providers;

public sealed class ArtifactStorageDriverFactoryCatalog : IArtifactStorageDriverFactoryCatalog
{
    private readonly IReadOnlyDictionary<string, IArtifactStorageDriverFactory> _byProviderTypeKey;

    public ArtifactStorageDriverFactoryCatalog(IEnumerable<IArtifactStorageDriverFactory> factories, IStorageProviderModuleCatalog moduleCatalog)
    {
        ArgumentNullException.ThrowIfNull(factories);
        ArgumentNullException.ThrowIfNull(moduleCatalog);

        var orderedFactories = factories.OrderBy(factory => factory?.ProviderTypeKey, StringComparer.Ordinal).ThenBy(factory => factory?.GetType().FullName, StringComparer.Ordinal).ToList();
        var byProviderTypeKey = new Dictionary<string, IArtifactStorageDriverFactory>(StringComparer.Ordinal);

        foreach (var factory in orderedFactories)
        {
            if (factory == null) throw new InvalidOperationException("Artifact storage driver factory catalog cannot contain a null factory.");
            if (string.IsNullOrWhiteSpace(factory.ProviderTypeKey))
                throw new InvalidOperationException($"Artifact storage driver factory '{factory.GetType().FullName}' has an empty {nameof(IArtifactStorageDriverFactory.ProviderTypeKey)}.");
            if (byProviderTypeKey.TryAdd(factory.ProviderTypeKey, factory)) continue;

            var incumbent = byProviderTypeKey[factory.ProviderTypeKey];
            throw new InvalidOperationException($"Artifact storage driver {nameof(IArtifactStorageDriverFactory.ProviderTypeKey)} '{factory.ProviderTypeKey}' is claimed by both '{incumbent.GetType().FullName}' and '{factory.GetType().FullName}'. Every provider type/version must have exactly one factory.");
        }

        var modules = moduleCatalog.Modules.OrderBy(module => module.TypeKey, StringComparer.Ordinal).ThenBy(module => module.GetType().FullName, StringComparer.Ordinal).ToList();
        foreach (var module in modules) ValidateModuleFactory(module, orderedFactories, byProviderTypeKey);

        var moduleKeys = modules.Select(module => module.TypeKey).ToHashSet(StringComparer.Ordinal);
        var orphan = orderedFactories.FirstOrDefault(factory => !moduleKeys.Contains(factory.ProviderTypeKey));
        if (orphan != null)
            throw new InvalidOperationException($"Artifact storage driver factory '{orphan.GetType().FullName}' declares {nameof(IArtifactStorageDriverFactory.ProviderTypeKey)} '{orphan.ProviderTypeKey}', but no installed storage provider module declares that key.");

        _byProviderTypeKey = byProviderTypeKey;
    }

    public IArtifactStorageDriverFactory? Get(string providerTypeKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerTypeKey);
        return _byProviderTypeKey.TryGetValue(providerTypeKey, out var factory) ? factory : null;
    }

    public IArtifactStorageDriverFactory Require(string providerTypeKey)
    {
        var factory = Get(providerTypeKey);
        if (factory != null) return factory;

        var available = _byProviderTypeKey.Count == 0 ? "none" : string.Join(", ", _byProviderTypeKey.Keys.OrderBy(key => key, StringComparer.Ordinal));
        throw new NotSupportedException($"Artifact storage driver factory for provider type '{providerTypeKey}' is not registered in this build. Available provider types: {available}.");
    }

    private static void ValidateModuleFactory(IStorageProviderModule module, IReadOnlyList<IArtifactStorageDriverFactory> factories, IReadOnlyDictionary<string, IArtifactStorageDriverFactory> byProviderTypeKey)
    {
        if (byProviderTypeKey.TryGetValue(module.TypeKey, out var factory))
        {
            if (factory.GetType() == module.FactoryType) return;
            throw new InvalidOperationException($"Storage provider module '{module.TypeKey}' declares factory type '{module.FactoryType.FullName}', but {nameof(IArtifactStorageDriverFactory.ProviderTypeKey)} '{module.TypeKey}' is registered by concrete type '{factory.GetType().FullName}'.");
        }

        var mismatched = factories.Where(candidate => candidate.GetType() == module.FactoryType).Select(candidate => candidate.ProviderTypeKey).OrderBy(key => key, StringComparer.Ordinal).ToList();
        if (mismatched.Count != 0)
            throw new InvalidOperationException($"Storage provider module '{module.TypeKey}' declares factory type '{module.FactoryType.FullName}', but that concrete type is registered with mismatched {nameof(IArtifactStorageDriverFactory.ProviderTypeKey)}: {string.Join(", ", mismatched)}.");

        throw new InvalidOperationException($"Storage provider module '{module.TypeKey}' declares factory type '{module.FactoryType.FullName}', but that factory is not registered as {nameof(IArtifactStorageDriverFactory)}.");
    }
}
