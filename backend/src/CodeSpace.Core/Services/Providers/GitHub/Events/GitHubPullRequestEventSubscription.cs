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
            "opened" => BuildOpened(repositoryId, deliveryId, now, pr, root),
            // Reopening re-enters the "needs review / CI" state. GitHub Actions bundles `reopened`
            // into its default pull_request trigger set alongside `opened`, so we fire the same event.
            "reopened" => BuildOpened(repositoryId, deliveryId, now, pr, root),
            "synchronize" => BuildSynchronized(repositoryId, deliveryId, now, pr, root),
            "closed" => pr.GetProperty("merged").GetBoolean()
                ? BuildMerged(repositoryId, deliveryId, now, pr, root)
                : BuildClosed(repositoryId, deliveryId, now, pr, root),
            _ => null
        };
    }

    private static PullRequestOpenedEvent BuildOpened(Guid repositoryId, string deliveryId, DateTimeOffset now, JsonElement pr, JsonElement root)
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
            HeadSha = TryReadSide(pr, "head", out var head) ? ReadString(head, "sha") : null,
            Labels = ExtractLabels(pr),
            IsDraft = ReadIsDraft(pr),
            Origin = ReadOrigin(pr, root)
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
        IsDraft = ReadIsDraft(pr),
        Origin = ReadOrigin(pr, root) with { PusherExternalId = ReadSenderId(root) }
    };

    /// <summary>On a synchronize, <c>sender</c> is whoever pushed the commits — not necessarily the PR's author. Null when the payload omits it.</summary>
    private static string? ReadSenderId(JsonElement root) =>
        root.TryGetProperty("sender", out var sender) && sender.ValueKind == JsonValueKind.Object && sender.TryGetProperty("id", out var id) ? id.GetRawText() : null;

    /// <summary>
    /// Who wrote the PR and where its head lives. Every field is optional on the wire: a payload that omits one reads as
    /// "not known", never as a member or a same-repository PR — except a head repository GitHub sends as <c>null</c>,
    /// which it does only for a fork that was since deleted.
    /// </summary>
    private static PullRequestOrigin ReadOrigin(JsonElement pr, JsonElement root) => new()
    {
        AuthorExternalId = pr.TryGetProperty("user", out var user) && user.ValueKind == JsonValueKind.Object && user.TryGetProperty("id", out var id) ? id.GetRawText() : null,
        AuthorAssociation = MapAuthorAssociation(ReadString(pr, "author_association")),
        IsFork = ReadIsFork(pr),
        HeadRepositoryFullName = TryReadSide(pr, "head", out var head) && head.TryGetProperty("repo", out var repo) && repo.ValueKind == JsonValueKind.Object ? ReadString(repo, "full_name") : null,
        RepositoryVisibility = root.TryGetProperty("repository", out var repository) && repository.ValueKind == JsonValueKind.Object ? ReadVisibility(repository) : null
    };

    /// <summary>
    /// GitHub's <c>author_association</c> onto the normalised standing. OWNER, MEMBER and COLLABORATOR hold a role on the
    /// repository or its organization; the contributor values have had changes merged but hold none; anything else is
    /// unknown rather than guessed.
    /// </summary>
    internal static PullRequestAuthorAssociation MapAuthorAssociation(string? raw) => raw switch
    {
        "OWNER" or "MEMBER" or "COLLABORATOR" => PullRequestAuthorAssociation.Member,
        "CONTRIBUTOR" or "FIRST_TIME_CONTRIBUTOR" or "FIRST_TIMER" => PullRequestAuthorAssociation.Contributor,
        "NONE" or "MANNEQUIN" => PullRequestAuthorAssociation.None,
        _ => PullRequestAuthorAssociation.Unknown
    };

    /// <summary>
    /// Whether the head lives in another repository than the base. Not <c>head.repo.fork</c>: that says the head
    /// REPOSITORY is a fork of something, which is true of every branch in an organization's own fork of upstream.
    /// </summary>
    private static bool ReadIsFork(JsonElement pr)
    {
        if (!TryReadSide(pr, "head", out var head) || !head.TryGetProperty("repo", out var headRepo)) return false;
        if (headRepo.ValueKind == JsonValueKind.Null) return true;
        if (!TryReadSide(pr, "base", out var @base) || !@base.TryGetProperty("repo", out var baseRepo)) return false;
        if (headRepo.ValueKind != JsonValueKind.Object || baseRepo.ValueKind != JsonValueKind.Object) return false;
        if (!headRepo.TryGetProperty("id", out var headId) || !baseRepo.TryGetProperty("id", out var baseId)) return false;

        return headId.GetRawText() != baseId.GetRawText();
    }

    /// <summary><c>repository.visibility</c> when GitHub sends it (it distinguishes internal), else the older <c>repository.private</c> flag.</summary>
    private static RepositoryVisibility? ReadVisibility(JsonElement repository) => ReadString(repository, "visibility") switch
    {
        "public" => RepositoryVisibility.Public,
        "internal" => RepositoryVisibility.Internal,
        "private" => RepositoryVisibility.Private,
        _ => repository.TryGetProperty("private", out var isPrivate) && isPrivate.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? (isPrivate.GetBoolean() ? RepositoryVisibility.Private : RepositoryVisibility.Public)
            : null
    };

    private static bool TryReadSide(JsonElement pr, string side, out JsonElement value) =>
        pr.TryGetProperty(side, out value) && value.ValueKind == JsonValueKind.Object;

    private static string? ReadString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

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
