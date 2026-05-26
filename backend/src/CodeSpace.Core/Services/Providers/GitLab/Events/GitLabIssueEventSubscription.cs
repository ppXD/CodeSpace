using System.Text.Json;
using CodeSpace.Core.Services.Providers.Events;
using CodeSpace.Messages.Enums;
using CodeSpace.Messages.Events;
using CodeSpace.Messages.Events.Issue;

namespace CodeSpace.Core.Services.Providers.GitLab.Events;

public sealed class GitLabIssueEventSubscription : IProviderEventSubscription
{
    public ProviderKind Kind => ProviderKind.GitLab;
    public string RawEventName => "Issue Hook";

    public NormalizedEvent? Normalize(Guid repositoryId, JsonElement root, IReadOnlyDictionary<string, string> headers)
    {
        var attrs = root.GetProperty("object_attributes");
        var action = attrs.GetProperty("action").GetString();
        var deliveryId = GitLabDelivery.IdFromHeadersOrFallback(headers);
        var now = DateTimeOffset.UtcNow;

        return action switch
        {
            "open" => BuildOpened(repositoryId, deliveryId, now, attrs, root),
            "close" => BuildClosed(repositoryId, deliveryId, now, attrs, root),
            _ => null
        };
    }

    private static IssueOpenedEvent BuildOpened(Guid repositoryId, string deliveryId, DateTimeOffset now, JsonElement attrs, JsonElement root)
    {
        var user = root.GetProperty("user");

        return new IssueOpenedEvent
        {
            RepositoryId = repositoryId,
            ProviderEventId = deliveryId,
            OccurredAt = now,
            ExternalIssueId = attrs.GetProperty("id").GetRawText(),
            Number = attrs.GetProperty("iid").GetInt32(),
            Title = attrs.GetProperty("title").GetString()!,
            Body = attrs.TryGetProperty("description", out var desc) && desc.ValueKind != JsonValueKind.Null ? desc.GetString() : null,
            AuthorExternalId = user.GetProperty("id").GetRawText(),
            AuthorName = user.GetProperty("username").GetString()!,
            WebUrl = attrs.GetProperty("url").GetString()!
        };
    }

    private static IssueClosedEvent BuildClosed(Guid repositoryId, string deliveryId, DateTimeOffset now, JsonElement attrs, JsonElement root)
    {
        var user = root.GetProperty("user");

        return new IssueClosedEvent
        {
            RepositoryId = repositoryId,
            ProviderEventId = deliveryId,
            OccurredAt = now,
            ExternalIssueId = attrs.GetProperty("id").GetRawText(),
            Number = attrs.GetProperty("iid").GetInt32(),
            ClosedByExternalId = user.GetProperty("id").GetRawText(),
            ClosedByName = user.GetProperty("username").GetString()!
        };
    }
}
