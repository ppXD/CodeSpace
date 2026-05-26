using CodeSpace.Messages.Enums;

namespace CodeSpace.Core.Services.Providers.Modules;

public sealed class ProviderModuleCatalog : IProviderModuleCatalog
{
    private readonly IReadOnlyDictionary<ProviderKind, IProviderModule> _byKind;

    public ProviderModuleCatalog(IEnumerable<IProviderModule> modules)
    {
        var list = modules.ToList();

        _byKind = list.ToDictionary(m => m.Kind);
        Modules = list;
    }

    public IReadOnlyList<IProviderModule> Modules { get; }

    public IProviderModule? Get(ProviderKind kind) => _byKind.TryGetValue(kind, out var m) ? m : null;
}
