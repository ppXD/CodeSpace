using CodeSpace.Core.Services.Workflows.Artifacts.Providers;
using CodeSpace.Messages.Dtos.Storage;
using CodeSpace.Messages.Queries.Storage;
using MediatR;

namespace CodeSpace.Core.Handlers.QueryHandlers.Storage;

/// <summary>
/// Pure catalog projection for the Settings discovery surface. It reads descriptor fields only: no storage profile,
/// credential, secret value, backend resolution, or <see cref="IStorageProviderModule.FactoryType"/> activation.
/// </summary>
public sealed class ListStorageProviderModulesQueryHandler : IRequestHandler<ListStorageProviderModulesQuery, IReadOnlyList<StorageProviderModuleDescriptor>>
{
    private readonly IStorageProviderModuleCatalog _catalog;

    public ListStorageProviderModulesQueryHandler(IStorageProviderModuleCatalog catalog) { _catalog = catalog; }

    public Task<IReadOnlyList<StorageProviderModuleDescriptor>> Handle(ListStorageProviderModulesQuery request, CancellationToken cancellationToken)
    {
        IReadOnlyList<StorageProviderModuleDescriptor> result = _catalog.Modules
            .OrderBy(module => module.TypeKey, StringComparer.Ordinal)
            .Select(module => new StorageProviderModuleDescriptor
            {
                TypeKey = module.TypeKey,
                DisplayName = module.DisplayName,
                ConfigSchema = module.ConfigSchema.Clone(),
                SecretSchema = module.SecretSchema.Clone(),
                Capabilities = ExpandCapabilities(module.Capabilities),
                TeamNamespaceProperty = (module as IStorageProviderTeamNamespace)?.TeamNamespaceProperty,
                AcceptsNoNewBytes = module is IStorageProviderAcceptsNoNewBytes,
            })
            .ToList();

        return Task.FromResult(result);
    }

    private static IReadOnlyList<string> ExpandCapabilities(StorageProviderCapabilities capabilities)
    {
        var values = Enum.GetValues<StorageProviderCapabilities>();
        var known = values.Aggregate(StorageProviderCapabilities.None, (mask, value) => mask | value);
        var unknown = capabilities & ~known;

        if (unknown != StorageProviderCapabilities.None)
            throw new InvalidOperationException($"Storage provider declares unknown capability bits: {(long)unknown}.");

        return values
            .Where(value => value != StorageProviderCapabilities.None && capabilities.HasFlag(value))
            .OrderBy(value => (long)value)
            .Select(value => value.ToString())
            .ToList();
    }
}
