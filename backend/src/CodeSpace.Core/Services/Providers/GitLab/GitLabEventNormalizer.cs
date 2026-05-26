using System.Text.Json;
using CodeSpace.Core.Services.Providers;
using CodeSpace.Core.Services.Providers.Events;
using CodeSpace.Messages.Enums;
using CodeSpace.Messages.Events;

namespace CodeSpace.Core.Services.Providers.GitLab;

/// <summary>
/// Facade — reads the GitLab event header, delegates parsing to the matching subscription
/// registered under (GitLab, rawEvent). See <see cref="GitHubEventNormalizer"/> sibling.
/// </summary>
public sealed class GitLabEventNormalizer
{
    private const string HeaderEvent = "X-Gitlab-Event";

    private readonly IProviderEventSubscriptionRegistry _subscriptions;

    public GitLabEventNormalizer(IProviderEventSubscriptionRegistry subscriptions) { _subscriptions = subscriptions; }

    public NormalizedEvent? Normalize(Guid repositoryId, string body, IReadOnlyDictionary<string, string> headers)
    {
        if (!WebhookHeaderLookup.TryFind(headers, HeaderEvent, out var rawEventName)) return null;

        var subscription = _subscriptions.Find(ProviderKind.GitLab, rawEventName);

        if (subscription == null) return null;

        using var doc = JsonDocument.Parse(body);
        return subscription.Normalize(repositoryId, doc.RootElement, headers);
    }
}
