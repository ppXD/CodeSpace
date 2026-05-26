using System.Text.Json;
using CodeSpace.Core.Services.Providers.Events;
using CodeSpace.Messages.Enums;
using CodeSpace.Messages.Events;
using CodeSpace.Messages.Events.PullRequest;

namespace CodeSpace.Core.Services.Providers.GitHub.Events;

public sealed class GitHubPullRequestEventSubscription : IProviderEventSubscription
{
    public ProviderKind Kind => ProviderKind.GitHub;
    public string RawEventName => "pull_request";

    public NormalizedEvent? Normalize(Guid repositoryId, JsonElement root, IReadOnlyDictionary<string, string> headers)
    {
        var action = root.GetProperty("action").GetString();
        var pr = root.GetProperty("pull_request");
        var deliveryId = GitHubDelivery.IdFromHeadersOrFallback(headers);
        var now = DateTimeOffset.UtcNow;

        return action switch
        {
            "opened" => BuildOpened(repositoryId, deliveryId, now, pr),
            "synchronize" => BuildSynchronized(repositoryId, deliveryId, now, pr, root),
            "closed" => pr.GetProperty("merged").GetBoolean()
                ? BuildMerged(repositoryId, deliveryId, now, pr, root)
                : BuildClosed(repositoryId, deliveryId, now, pr, root),
            _ => null
        };
    }

    private static PullRequestOpenedEvent BuildOpened(Guid repositoryId, string deliveryId, DateTimeOffset now, JsonElement pr)
    {
        var user = pr.GetProperty("user");

        return new PullRequestOpenedEvent
        {
            RepositoryId = repositoryId,
            ProviderEventId = deliveryId,
            OccurredAt = now,
            ExternalPullRequestId = pr.GetProperty("id").GetRawText(),
            Number = pr.GetProperty("number").GetInt32(),
            Title = pr.GetProperty("title").GetString()!,
            Body = pr.TryGetProperty("body", out var bodyEl) && bodyEl.ValueKind != JsonValueKind.Null ? bodyEl.GetString() : null,
            SourceBranch = pr.GetProperty("head").GetProperty("ref").GetString()!,
            TargetBranch = pr.GetProperty("base").GetProperty("ref").GetString()!,
            AuthorExternalId = user.GetProperty("id").GetRawText(),
            AuthorName = user.GetProperty("login").GetString()!,
            WebUrl = pr.GetProperty("html_url").GetString()!
        };
    }

    private static PullRequestSynchronizedEvent BuildSynchronized(Guid repositoryId, string deliveryId, DateTimeOffset now, JsonElement pr, JsonElement root) => new()
    {
        RepositoryId = repositoryId,
        ProviderEventId = deliveryId,
        OccurredAt = now,
        ExternalPullRequestId = pr.GetProperty("id").GetRawText(),
        Number = pr.GetProperty("number").GetInt32(),
        PreviousHeadSha = root.GetProperty("before").GetString()!,
        NewHeadSha = root.GetProperty("after").GetString()!
    };

    private static PullRequestMergedEvent BuildMerged(Guid repositoryId, string deliveryId, DateTimeOffset now, JsonElement pr, JsonElement root)
    {
        var sender = root.GetProperty("sender");

        return new PullRequestMergedEvent
        {
            RepositoryId = repositoryId,
            ProviderEventId = deliveryId,
            OccurredAt = now,
            ExternalPullRequestId = pr.GetProperty("id").GetRawText(),
            Number = pr.GetProperty("number").GetInt32(),
            MergedByExternalId = sender.GetProperty("id").GetRawText(),
            MergedByName = sender.GetProperty("login").GetString()!,
            MergeCommitSha = pr.TryGetProperty("merge_commit_sha", out var sha) && sha.ValueKind != JsonValueKind.Null ? sha.GetString() : null
        };
    }

    private static PullRequestClosedEvent BuildClosed(Guid repositoryId, string deliveryId, DateTimeOffset now, JsonElement pr, JsonElement root)
    {
        var sender = root.GetProperty("sender");

        return new PullRequestClosedEvent
        {
            RepositoryId = repositoryId,
            ProviderEventId = deliveryId,
            OccurredAt = now,
            ExternalPullRequestId = pr.GetProperty("id").GetRawText(),
            Number = pr.GetProperty("number").GetInt32(),
            ClosedByExternalId = sender.GetProperty("id").GetRawText(),
            ClosedByName = sender.GetProperty("login").GetString()!
        };
    }
}
