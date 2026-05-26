using CodeSpace.Core.DependencyInjection;
using CodeSpace.Messages.Enums;

namespace CodeSpace.Core.Services.Providers.Events;

public sealed class ProviderEventSubscriptionRegistry : IProviderEventSubscriptionRegistry, IScopedDependency
{
    private readonly IReadOnlyDictionary<(ProviderKind Kind, string RawEventName), IProviderEventSubscription> _byPair;
    private readonly IReadOnlyDictionary<ProviderKind, IReadOnlyList<string>> _rawEventsByKind;

    public ProviderEventSubscriptionRegistry(IEnumerable<IProviderEventSubscription> subscriptions)
    {
        var list = subscriptions.ToList();

        _byPair = list.ToDictionary(s => (s.Kind, s.RawEventName));
        _rawEventsByKind = list.GroupBy(s => s.Kind).ToDictionary(g => g.Key, g => (IReadOnlyList<string>)g.Select(s => s.RawEventName).ToList());
    }

    public IReadOnlyList<string> GetSubscribedRawEvents(ProviderKind kind) => _rawEventsByKind.TryGetValue(kind, out var events) ? events : Array.Empty<string>();

    public IProviderEventSubscription? Find(ProviderKind kind, string rawEventName) => _byPair.TryGetValue((kind, rawEventName), out var s) ? s : null;
}
