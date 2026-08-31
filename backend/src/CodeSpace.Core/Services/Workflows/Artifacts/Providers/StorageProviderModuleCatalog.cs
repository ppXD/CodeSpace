using System.Text.Json;
using System.Text.RegularExpressions;

namespace CodeSpace.Core.Services.Workflows.Artifacts.Providers;

public sealed class StorageProviderModuleCatalog : IStorageProviderModuleCatalog
{
    private static readonly Regex TypeKeyPattern = new("^[a-z0-9][a-z0-9.-]*/v[1-9][0-9]*$", RegexOptions.CultureInvariant);
    private readonly IReadOnlyDictionary<string, IStorageProviderModule> _byTypeKey;

    public StorageProviderModuleCatalog(IEnumerable<IStorageProviderModule> modules)
    {
        ArgumentNullException.ThrowIfNull(modules);

        var list = modules.OrderBy(m => m?.TypeKey, StringComparer.Ordinal).ThenBy(m => m?.GetType().FullName, StringComparer.Ordinal).ToList();
        var byTypeKey = new Dictionary<string, IStorageProviderModule>(StringComparer.Ordinal);

        foreach (var module in list)
        {
            Validate(module);

            if (byTypeKey.TryAdd(module.TypeKey, module)) continue;

            var incumbent = byTypeKey[module.TypeKey];
            throw new InvalidOperationException($"Storage provider TypeKey '{module.TypeKey}' is claimed by both '{incumbent.DisplayName}' ({incumbent.GetType().FullName}) and '{module.DisplayName}' ({module.GetType().FullName}). Every provider type/version must have exactly one module.");
        }

        Modules = list.AsReadOnly();
        _byTypeKey = byTypeKey;
    }

    public IReadOnlyList<IStorageProviderModule> Modules { get; }

    public IStorageProviderModule? Get(string typeKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(typeKey);
        return _byTypeKey.TryGetValue(typeKey, out var module) ? module : null;
    }

    public IStorageProviderModule Require(string typeKey)
    {
        var module = Get(typeKey);
        if (module != null) return module;

        var available = Modules.Count == 0 ? "none" : string.Join(", ", Modules.Select(m => m.TypeKey).OrderBy(k => k, StringComparer.Ordinal));
        throw new NotSupportedException($"Storage provider type '{typeKey}' is not registered in this build. Available provider types: {available}.");
    }

    private static void Validate(IStorageProviderModule? module)
    {
        if (module == null) throw new InvalidOperationException("Storage provider module catalog cannot contain a null module.");
        if (!TypeKeyPattern.IsMatch(module.TypeKey ?? string.Empty))
            throw new InvalidOperationException($"Storage provider module '{module.GetType().FullName}' has invalid TypeKey '{module.TypeKey}'. Use a canonical open versioned key such as 'aliyun-oss/v1'.");
        if (string.IsNullOrWhiteSpace(module.DisplayName))
            throw new InvalidOperationException($"Storage provider module '{module.TypeKey}' has an empty DisplayName.");
        if (module.ConfigSchema.ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException($"Storage provider module '{module.TypeKey}' {nameof(IStorageProviderModule.ConfigSchema)} must be a JSON object schema.");
        if (module.SecretSchema.ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException($"Storage provider module '{module.TypeKey}' {nameof(IStorageProviderModule.SecretSchema)} must be a JSON object schema.");
        if (module.FactoryType == null || !module.FactoryType.IsClass || module.FactoryType.IsAbstract || !typeof(IArtifactStorageDriverFactory).IsAssignableFrom(module.FactoryType))
            throw new InvalidOperationException($"Storage provider module '{module.TypeKey}' FactoryType must be a concrete {nameof(IArtifactStorageDriverFactory)} implementation.");

        // Every delete path in the plane requires Delete of the driver before it asks the destination for anything,
        // so refusing the pair here is what makes the marker mean something: a provider whose keys are shared across
        // teams can never declare its way into removing bytes a team it has never heard of is still pointing at.
        if (module is IStorageProviderTenantSharedObjectKeys && module.Capabilities.HasFlag(StorageProviderCapabilities.Delete))
            throw new InvalidOperationException($"Storage provider module '{module.TypeKey}' declares {nameof(IStorageProviderTenantSharedObjectKeys)} and {nameof(StorageProviderCapabilities)}.{nameof(StorageProviderCapabilities.Delete)} together. One object key names bytes every team shares, so removing one is a cross-team act no team-scoped caller can authorize.");
    }
}
