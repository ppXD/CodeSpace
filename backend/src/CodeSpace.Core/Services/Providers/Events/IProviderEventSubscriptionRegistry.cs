using CodeSpace.Messages.Enums;

namespace CodeSpace.Core.Services.Providers.Events;

public interface IProviderEventSubscriptionRegistry
{
    /// <summary>Returns the raw event names this provider currently has subscriptions for. Used by bind to construct the WebhookRegistration.SubscribedEvents list.</summary>
    IReadOnlyList<string> GetSubscribedRawEvents(ProviderKind kind);

    /// <summary>Returns the subscription handling (kind, rawEventName), or null when no subscription is registered (caller treats as "uninteresting event").</summary>
    IProviderEventSubscription? Find(ProviderKind kind, string rawEventName);
}
