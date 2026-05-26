using System.Text.Json;
using CodeSpace.Core.Services.Providers.Events;
using CodeSpace.Messages.Enums;
using CodeSpace.Messages.Events;
using CodeSpace.Messages.Events.Issue;

namespace CodeSpace.Core.Services.Providers.GitHub.Events;

public sealed class GitHubIssuesEventSubscription : IProviderEventSubscription
{
    public ProviderKind Kind => ProviderKind.GitHub;
    public string RawEventName => "issues";

    public NormalizedEvent? Normalize(Guid repositoryId, JsonElement root, IReadOnlyDictionary<string, string> headers)
    {
        var action = root.GetProperty("action").GetString();
        var issue = root.GetProperty("issue");
        var deliveryId = GitHubDelivery.IdFromHeadersOrFallback(headers);
        var now = DateTimeOffset.UtcNow;

        return action switch
        {
            "opened" => BuildOpened(repositoryId, deliveryId, now, issue),
            "closed" => BuildClosed(repositoryId, deliveryId, now, issue, root),
            _ => null
        };
    }

    private static IssueOpenedEvent BuildOpened(Guid repositoryId, string deliveryId, DateTimeOffset now, JsonElement issue)
    {
        var user = issue.GetProperty("user");

        return new IssueOpenedEvent
        {
            RepositoryId = repositoryId,
            ProviderEventId = deliveryId,
            OccurredAt = now,
            ExternalIssueId = issue.GetProperty("id").GetRawText(),
            Number = issue.GetProperty("number").GetInt32(),
            Title = issue.GetProperty("title").GetString()!,
            Body = issue.TryGetProperty("body", out var bodyEl) && bodyEl.ValueKind != JsonValueKind.Null ? bodyEl.GetString() : null,
            AuthorExternalId = user.GetProperty("id").GetRawText(),
            AuthorName = user.GetProperty("login").GetString()!,
            WebUrl = issue.GetProperty("html_url").GetString()!
        };
    }

    private static IssueClosedEvent BuildClosed(Guid repositoryId, string deliveryId, DateTimeOffset now, JsonElement issue, JsonElement root)
    {
        var sender = root.GetProperty("sender");

        return new IssueClosedEvent
        {
            RepositoryId = repositoryId,
            ProviderEventId = deliveryId,
            OccurredAt = now,
            ExternalIssueId = issue.GetProperty("id").GetRawText(),
            Number = issue.GetProperty("number").GetInt32(),
            ClosedByExternalId = sender.GetProperty("id").GetRawText(),
            ClosedByName = sender.GetProperty("login").GetString()!
        };
    }
}
