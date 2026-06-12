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
            // Reopening re-enters the "needs review / CI" state. GitHub Actions bundles `reopened`
            // into its default pull_request trigger set alongside `opened`, so we fire the same event.
            "reopened" => BuildOpened(repositoryId, deliveryId, now, pr),
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
            WebUrl = pr.GetProperty("html_url").GetString()!,
            Labels = ExtractLabels(pr),
            IsDraft = ReadIsDraft(pr)
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
        NewHeadSha = root.GetProperty("after").GetString()!,
        Labels = ExtractLabels(pr),
        IsDraft = ReadIsDraft(pr)
    };

    /// <summary>
    /// GitHub sets <c>pull_request.draft = true</c> while a PR is a draft. Absent / non-boolean →
    /// false (treat as ready), so a provider that omits the field never makes a PR look like a draft.
    /// </summary>
    private static bool ReadIsDraft(JsonElement pr) =>
        pr.TryGetProperty("draft", out var draft) && draft.ValueKind == JsonValueKind.True;

    /// <summary>
    /// GitHub PR webhooks include the full label state at <c>pull_request.labels[]</c> on
    /// every action variant (opened / synchronize / labeled / unlabeled / closed). Each entry
    /// is <c>{ id, node_id, name, color, default, description }</c>. We surface names only;
    /// the matcher matches on names and downstream nodes reference <c>{{trigger.labels}}</c>
    /// as a string array. Skips entries whose <c>name</c> is null/empty so the array
    /// downstream stays clean.
    /// </summary>
    private static IReadOnlyList<string> ExtractLabels(JsonElement pr)
    {
        if (!pr.TryGetProperty("labels", out var labels) || labels.ValueKind != JsonValueKind.Array) return Array.Empty<string>();

        var names = new List<string>(labels.GetArrayLength());

        foreach (var label in labels.EnumerateArray())
        {
            if (label.ValueKind != JsonValueKind.Object) continue;
            if (!label.TryGetProperty("name", out var nameEl)) continue;
            if (nameEl.ValueKind != JsonValueKind.String) continue;

            var name = nameEl.GetString();
            if (!string.IsNullOrEmpty(name)) names.Add(name);
        }

        return names;
    }

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
            MergeCommitSha = pr.TryGetProperty("merge_commit_sha", out var sha) && sha.ValueKind != JsonValueKind.Null ? sha.GetString() : null,
            Labels = ExtractLabels(pr)
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
