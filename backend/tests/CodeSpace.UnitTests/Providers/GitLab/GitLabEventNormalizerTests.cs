using CodeSpace.Core.Services.Providers.Events;
using CodeSpace.Core.Services.Providers.GitLab;
using CodeSpace.Core.Services.Providers.GitLab.Events;
using CodeSpace.Messages.Events.Issue;
using CodeSpace.Messages.Events.PullRequest;
using CodeSpace.Messages.Events.Push;
using Shouldly;

namespace CodeSpace.UnitTests.Providers.GitLab;

public class GitLabEventNormalizerTests
{
    private readonly GitLabEventNormalizer _normalizer = new(new ProviderEventSubscriptionRegistry(new IProviderEventSubscription[]
    {
        new GitLabPushEventSubscription(),
        new GitLabMergeRequestEventSubscription(),
        new GitLabIssueEventSubscription()
    }));
    private readonly Guid _repositoryId = Guid.NewGuid();

    [Fact]
    public void Normalize_returns_null_when_event_header_missing()
    {
        _normalizer.Normalize(_repositoryId, "{}", new Dictionary<string, string>()).ShouldBeNull();
    }

    [Fact]
    public void Normalize_returns_null_for_unhandled_event_type()
    {
        var headers = new Dictionary<string, string> { ["X-Gitlab-Event"] = "Wiki Page Hook" };

        _normalizer.Normalize(_repositoryId, "{}", headers).ShouldBeNull();
    }

    [Fact]
    public void Normalize_push_hook_returns_PushReceivedEvent()
    {
        var headers = new Dictionary<string, string>
        {
            ["X-Gitlab-Event"] = "Push Hook",
            ["X-Gitlab-Event-UUID"] = "uuid-123"
        };
        var body = """
            {
              "object_kind": "push",
              "before": "before-sha",
              "after": "after-sha",
              "ref": "refs/heads/main",
              "user_id": 4,
              "user_name": "John Doe",
              "user_username": "jsmith",
              "commits": [
                { "id": "abc", "message": "Update", "author": { "name": "Jane", "email": "j@x" } }
              ]
            }
            """;

        var result = _normalizer.Normalize(_repositoryId, body, headers).ShouldBeOfType<PushReceivedEvent>();

        result.RepositoryId.ShouldBe(_repositoryId);
        result.ProviderEventId.ShouldBe("uuid-123");
        result.Ref.ShouldBe("refs/heads/main");
        result.BeforeSha.ShouldBe("before-sha");
        result.AfterSha.ShouldBe("after-sha");
        result.PusherExternalId.ShouldBe("4");
        result.PusherName.ShouldBe("John Doe");
        result.Commits.Count.ShouldBe(1);
    }

    [Fact]
    public void Normalize_merge_request_open_returns_PullRequestOpenedEvent()
    {
        var headers = new Dictionary<string, string> { ["X-Gitlab-Event"] = "Merge Request Hook" };
        var body = """
            {
              "object_kind": "merge_request",
              "user": { "id": 1, "username": "alice" },
              "object_attributes": {
                "id": 99,
                "iid": 5,
                "title": "MR Title",
                "description": "Description",
                "source_branch": "feature",
                "target_branch": "main",
                "action": "open",
                "url": "https://gitlab.com/acme/repo/-/merge_requests/5"
              }
            }
            """;

        var result = _normalizer.Normalize(_repositoryId, body, headers).ShouldBeOfType<PullRequestOpenedEvent>();

        result.ExternalPullRequestId.ShouldBe("99");
        result.Number.ShouldBe(5);
        result.Title.ShouldBe("MR Title");
        result.Body.ShouldBe("Description");
        result.SourceBranch.ShouldBe("feature");
        result.TargetBranch.ShouldBe("main");
        result.AuthorExternalId.ShouldBe("1");
        result.AuthorName.ShouldBe("alice");
        result.WebUrl.ShouldBe("https://gitlab.com/acme/repo/-/merge_requests/5");
    }

    [Fact]
    public void Normalize_merge_request_update_returns_PullRequestSynchronizedEvent()
    {
        var headers = new Dictionary<string, string> { ["X-Gitlab-Event"] = "Merge Request Hook" };
        var body = """
            {
              "object_kind": "merge_request",
              "user": { "id": 1, "username": "u" },
              "object_attributes": {
                "id": 99, "iid": 5, "title": "x", "url": "x",
                "source_branch": "f", "target_branch": "main",
                "action": "update",
                "oldrev": "old-sha",
                "last_commit": { "id": "new-sha" }
              }
            }
            """;

        var result = _normalizer.Normalize(_repositoryId, body, headers).ShouldBeOfType<PullRequestSynchronizedEvent>();

        result.PreviousHeadSha.ShouldBe("old-sha");
        result.NewHeadSha.ShouldBe("new-sha");
        result.Number.ShouldBe(5);
    }

    [Fact]
    public void Normalize_merge_request_merge_returns_PullRequestMergedEvent()
    {
        var headers = new Dictionary<string, string> { ["X-Gitlab-Event"] = "Merge Request Hook" };
        var body = """
            {
              "object_kind": "merge_request",
              "user": { "id": 2, "username": "merger" },
              "object_attributes": {
                "id": 99, "iid": 5, "title": "x", "url": "x",
                "source_branch": "f", "target_branch": "main",
                "action": "merge",
                "merge_commit_sha": "merge-sha"
              }
            }
            """;

        var result = _normalizer.Normalize(_repositoryId, body, headers).ShouldBeOfType<PullRequestMergedEvent>();

        result.MergedByExternalId.ShouldBe("2");
        result.MergedByName.ShouldBe("merger");
        result.MergeCommitSha.ShouldBe("merge-sha");
    }

    [Fact]
    public void Normalize_merge_request_close_returns_PullRequestClosedEvent()
    {
        var headers = new Dictionary<string, string> { ["X-Gitlab-Event"] = "Merge Request Hook" };
        var body = """
            {
              "object_kind": "merge_request",
              "user": { "id": 3, "username": "closer" },
              "object_attributes": {
                "id": 99, "iid": 5, "title": "x", "url": "x",
                "source_branch": "f", "target_branch": "main",
                "action": "close"
              }
            }
            """;

        var result = _normalizer.Normalize(_repositoryId, body, headers).ShouldBeOfType<PullRequestClosedEvent>();

        result.ClosedByExternalId.ShouldBe("3");
        result.ClosedByName.ShouldBe("closer");
    }

    [Fact]
    public void Normalize_merge_request_with_unknown_action_returns_null()
    {
        var headers = new Dictionary<string, string> { ["X-Gitlab-Event"] = "Merge Request Hook" };
        var body = """
            {
              "object_kind": "merge_request",
              "user": { "id": 1, "username": "u" },
              "object_attributes": { "id": 99, "iid": 5, "action": "approval" }
            }
            """;

        _normalizer.Normalize(_repositoryId, body, headers).ShouldBeNull();
    }

    [Fact]
    public void Normalize_issue_open_returns_IssueOpenedEvent()
    {
        var headers = new Dictionary<string, string> { ["X-Gitlab-Event"] = "Issue Hook" };
        var body = """
            {
              "object_kind": "issue",
              "user": { "id": 1, "username": "reporter" },
              "object_attributes": {
                "id": 100,
                "iid": 3,
                "title": "Bug",
                "description": "Body",
                "action": "open",
                "url": "https://gitlab.com/acme/repo/-/issues/3"
              }
            }
            """;

        var result = _normalizer.Normalize(_repositoryId, body, headers).ShouldBeOfType<IssueOpenedEvent>();

        result.ExternalIssueId.ShouldBe("100");
        result.Number.ShouldBe(3);
        result.Title.ShouldBe("Bug");
        result.Body.ShouldBe("Body");
        result.AuthorName.ShouldBe("reporter");
    }

    [Fact]
    public void Normalize_issue_close_returns_IssueClosedEvent()
    {
        var headers = new Dictionary<string, string> { ["X-Gitlab-Event"] = "Issue Hook" };
        var body = """
            {
              "object_kind": "issue",
              "user": { "id": 7, "username": "closer" },
              "object_attributes": {
                "id": 100, "iid": 3, "title": "x", "url": "x",
                "action": "close"
              }
            }
            """;

        var result = _normalizer.Normalize(_repositoryId, body, headers).ShouldBeOfType<IssueClosedEvent>();

        result.ClosedByExternalId.ShouldBe("7");
        result.ClosedByName.ShouldBe("closer");
    }

    [Fact]
    public void Normalize_issue_with_null_description_returns_event_with_null_body()
    {
        var headers = new Dictionary<string, string> { ["X-Gitlab-Event"] = "Issue Hook" };
        var body = """
            {
              "object_kind": "issue",
              "user": { "id": 1, "username": "u" },
              "object_attributes": {
                "id": 100, "iid": 3, "title": "x", "url": "x",
                "description": null,
                "action": "open"
              }
            }
            """;

        var result = _normalizer.Normalize(_repositoryId, body, headers).ShouldBeOfType<IssueOpenedEvent>();

        result.Body.ShouldBeNull();
    }
}
