using System.Text.Json;
using CodeSpace.Core.Services.Providers.Events;
using CodeSpace.Messages.Enums;
using CodeSpace.Messages.Events;
using CodeSpace.Messages.Events.PullRequest;

namespace CodeSpace.Core.Services.Providers.GitLab.Events;

public sealed class GitLabMergeRequestEventSubscription : IProviderEventSubscription
{
    public ProviderKind Kind => ProviderKind.GitLab;
    public string RawEventName => "Merge Request Hook";

    public NormalizedEvent? Normalize(Guid repositoryId, JsonElement root, IReadOnlyDictionary<string, string> headers)
    {
        var attrs = root.GetProperty("object_attributes");
        var action = attrs.GetProperty("action").GetString();
        var deliveryId = GitLabDelivery.IdFromHeadersOrFallback(headers);
        var now = DateTimeOffset.UtcNow;

        return action switch
        {
            "open" => BuildOpened(repositoryId, deliveryId, now, attrs, root),
            // GitLab fires action:"reopen" when an MR is reopened — same re-enters-review semantics as
            // `open` (and GitHub's `reopened`), so we map it to the same opened event.
            "reopen" => BuildOpened(repositoryId, deliveryId, now, attrs, root),
            "update" => HasCodeChange(attrs) ? BuildSynchronized(repositoryId, deliveryId, now, attrs, root) : null,
            "merge" => BuildMerged(repositoryId, deliveryId, now, attrs, root),
            "close" => BuildClosed(repositoryId, deliveryId, now, attrs, root),
            _ => null
        };
    }

    private static PullRequestOpenedEvent BuildOpened(Guid repositoryId, string deliveryId, DateTimeOffset now, JsonElement attrs, JsonElement root)
    {
        var user = root.GetProperty("user");

        return new PullRequestOpenedEvent
        {
            RepositoryId = repositoryId,
            ProviderEventId = deliveryId,
            OccurredAt = now,
            ExternalPullRequestId = attrs.GetProperty("id").GetRawText(),
            Number = attrs.GetProperty("iid").GetInt32(),
            Title = attrs.GetProperty("title").GetString()!,
            Body = attrs.TryGetProperty("description", out var desc) && desc.ValueKind != JsonValueKind.Null ? desc.GetString() : null,
            SourceBranch = attrs.GetProperty("source_branch").GetString()!,
            TargetBranch = attrs.GetProperty("target_branch").GetString()!,
            AuthorExternalId = user.GetProperty("id").GetRawText(),
            AuthorName = user.GetProperty("username").GetString()!,
            WebUrl = attrs.GetProperty("url").GetString()!,
            Labels = ExtractLabels(root),
            IsDraft = ReadIsDraft(attrs)
        };
    }

    /// <summary>
    /// GitLab fires <c>action:"update"</c> for ANY merge-request mutation — label / assignee /
    /// description / milestone edits AND code pushes alike. It includes
    /// <c>object_attributes.oldrev</c> ONLY when the source-branch HEAD actually moved (new
    /// commits were pushed). We therefore map an <c>update</c> to a code-change
    /// (<see cref="PullRequestSynchronizedEvent"/>) exclusively when <c>oldrev</c> is present;
    /// metadata-only edits return null and are ignored — mirroring how GitHub keeps its
    /// <c>synchronize</c> action (a push) distinct from <c>labeled</c>/<c>edited</c> (which we
    /// also ignore). Without this gate every label/assignee edit would spuriously fire a
    /// <c>trigger.pr.updated</c> run with empty head SHAs.
    /// </summary>
    private static bool HasCodeChange(JsonElement attrs) =>
        attrs.TryGetProperty("oldrev", out var oldRev) && oldRev.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(oldRev.GetString());

    private static PullRequestSynchronizedEvent BuildSynchronized(Guid repositoryId, string deliveryId, DateTimeOffset now, JsonElement attrs, JsonElement root)
    {
        var oldRev = attrs.TryGetProperty("oldrev", out var old) && old.ValueKind != JsonValueKind.Null ? old.GetString() ?? string.Empty : string.Empty;
        var newRev = attrs.TryGetProperty("last_commit", out var lc) && lc.ValueKind != JsonValueKind.Null ? lc.GetProperty("id").GetString() ?? string.Empty : string.Empty;

        return new PullRequestSynchronizedEvent
        {
            RepositoryId = repositoryId,
            ProviderEventId = deliveryId,
            OccurredAt = now,
            ExternalPullRequestId = attrs.GetProperty("id").GetRawText(),
            Number = attrs.GetProperty("iid").GetInt32(),
            PreviousHeadSha = oldRev,
            NewHeadSha = newRev,
            Labels = ExtractLabels(root),
            IsDraft = ReadIsDraft(attrs)
        };
    }

    /// <summary>
    /// GitLab marks a draft MR with <c>object_attributes.draft = true</c> (GitLab 13.2+), or the
    /// legacy <c>work_in_progress</c> on older instances. Either being true → draft; absent /
    /// non-boolean → false. Reading both keeps older self-hosted GitLab instances working.
    /// </summary>
    private static bool ReadIsDraft(JsonElement attrs) =>
        (attrs.TryGetProperty("draft", out var draft) && draft.ValueKind == JsonValueKind.True)
        || (attrs.TryGetProperty("work_in_progress", out var wip) && wip.ValueKind == JsonValueKind.True);

    /// <summary>
    /// GitLab Merge Request Hook payloads place the authoritative current label set at the
    /// top-level <c>labels[]</c> array — each entry is <c>{ id, title, color, project_id, ... }</c>.
    /// We surface titles only; the matcher matches on names and downstream nodes reference
    /// <c>{{trigger.labels}}</c> as a string array. <c>changes.labels.current</c> is
    /// deliberately NOT consulted — it's only populated when labels changed in this specific
    /// event, while the top-level array is the snapshot we want for matcher purposes.
    /// </summary>
    private static IReadOnlyList<string> ExtractLabels(JsonElement root)
    {
        if (!root.TryGetProperty("labels", out var labels) || labels.ValueKind != JsonValueKind.Array) return Array.Empty<string>();

        var titles = new List<string>(labels.GetArrayLength());

        foreach (var label in labels.EnumerateArray())
        {
            if (label.ValueKind != JsonValueKind.Object) continue;
            if (!label.TryGetProperty("title", out var titleEl)) continue;
            if (titleEl.ValueKind != JsonValueKind.String) continue;

            var title = titleEl.GetString();
            if (!string.IsNullOrEmpty(title)) titles.Add(title);
        }

        return titles;
    }

    private static PullRequestMergedEvent BuildMerged(Guid repositoryId, string deliveryId, DateTimeOffset now, JsonElement attrs, JsonElement root)
    {
        var user = root.GetProperty("user");

        return new PullRequestMergedEvent
        {
            RepositoryId = repositoryId,
            ProviderEventId = deliveryId,
            OccurredAt = now,
            ExternalPullRequestId = attrs.GetProperty("id").GetRawText(),
            Number = attrs.GetProperty("iid").GetInt32(),
            MergedByExternalId = user.GetProperty("id").GetRawText(),
            MergedByName = user.GetProperty("username").GetString()!,
            MergeCommitSha = attrs.TryGetProperty("merge_commit_sha", out var sha) && sha.ValueKind != JsonValueKind.Null ? sha.GetString() : null,
            Labels = ExtractLabels(root)
        };
    }

    private static PullRequestClosedEvent BuildClosed(Guid repositoryId, string deliveryId, DateTimeOffset now, JsonElement attrs, JsonElement root)
    {
        var user = root.GetProperty("user");

        return new PullRequestClosedEvent
        {
            RepositoryId = repositoryId,
            ProviderEventId = deliveryId,
            OccurredAt = now,
            ExternalPullRequestId = attrs.GetProperty("id").GetRawText(),
            Number = attrs.GetProperty("iid").GetInt32(),
            ClosedByExternalId = user.GetProperty("id").GetRawText(),
            ClosedByName = user.GetProperty("username").GetString()!
        };
    }
}
