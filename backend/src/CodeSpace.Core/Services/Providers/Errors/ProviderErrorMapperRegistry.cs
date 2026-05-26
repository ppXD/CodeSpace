using CodeSpace.Core.DependencyInjection;
using CodeSpace.Messages.Enums;

namespace CodeSpace.Core.Services.Providers.Errors;

public sealed class ProviderErrorMapperRegistry : IProviderErrorMapperRegistry, ISingletonDependency
{
    private readonly IReadOnlyDictionary<ProviderKind, IProviderErrorMapper> _byKind;

    public ProviderErrorMapperRegistry(IEnumerable<IProviderErrorMapper> mappers)
    {
        _byKind = mappers.ToDictionary(m => m.Kind);
    }

    public IProviderErrorMapper? Get(ProviderKind kind) => _byKind.TryGetValue(kind, out var m) ? m : null;
}
