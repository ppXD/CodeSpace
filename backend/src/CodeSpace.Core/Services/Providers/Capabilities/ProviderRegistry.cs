using CodeSpace.Core.DependencyInjection;
using CodeSpace.Messages.Enums;

namespace CodeSpace.Core.Services.Providers.Capabilities;

public sealed class ProviderRegistry : IProviderRegistry, IScopedDependency
{
    private readonly ILookup<ProviderKind, IProviderCapability> _byKind;

    public ProviderRegistry(IEnumerable<IProviderCapability> capabilities) { _byKind = capabilities.ToLookup(c => c.Kind); }

    public TCapability Require<TCapability>(ProviderKind kind) where TCapability : IProviderCapability
    {
        var match = _byKind[kind].OfType<TCapability>().FirstOrDefault();

        if (match == null) throw new NotSupportedException($"Provider {kind} does not implement capability {typeof(TCapability).Name}");

        return match;
    }

    public bool TryGet<TCapability>(ProviderKind kind, out TCapability? capability) where TCapability : class, IProviderCapability
    {
        capability = _byKind[kind].OfType<TCapability>().FirstOrDefault();
        return capability != null;
    }

    public IReadOnlyList<Type> GetCapabilities(ProviderKind kind)
    {
        return _byKind[kind]
            .SelectMany(c => c.GetType().GetInterfaces())
            .Where(i => i != typeof(IProviderCapability) && typeof(IProviderCapability).IsAssignableFrom(i))
            .Distinct()
            .ToList();
    }
}
