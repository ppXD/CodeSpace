using System.Text.Json;
using CodeSpace.Core.Services.Providers.Events;
using CodeSpace.Messages.Enums;
using CodeSpace.Messages.Events;

namespace CodeSpace.Core.Services.Providers.GitHub;

/// <summary>
/// Facade — reads the GitHub event header, delegates the actual payload parsing to the
/// matching IProviderEventSubscription registered under (GitHub, rawEvent). The split lets
/// each event type live in its own focused class.
/// </summary>
public sealed class GitHubEventNormalizer
{
    private const string HeaderEvent = "X-GitHub-Event";

    private readonly IProviderEventSubscriptionRegistry _subscriptions;

    public GitHubEventNormalizer(IProviderEventSubscriptionRegistry subscriptions) { _subscriptions = subscriptions; }

    public NormalizedEvent? Normalize(Guid repositoryId, string body, IReadOnlyDictionary<string, string> headers)
    {
        if (!WebhookHeaderLookup.TryFind(headers, HeaderEvent, out var rawEventName)) return null;

        var subscription = _subscriptions.Find(ProviderKind.GitHub, rawEventName);

        if (subscription == null) return null;

        using var doc = JsonDocument.Parse(body);
        return subscription.Normalize(repositoryId, doc.RootElement, headers);
    }
}
