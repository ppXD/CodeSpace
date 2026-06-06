using System.Text.Json;
using CodeSpace.Core.Services.Providers.Events;
using CodeSpace.Core.Services.Providers.GitHub;
using CodeSpace.Core.Services.Providers.GitHub.Events;
using CodeSpace.Messages.Events.Issue;
using CodeSpace.Messages.Events.PullRequest;
using CodeSpace.Messages.Events.Push;
using Shouldly;

namespace CodeSpace.UnitTests.Providers.GitHub;

[Trait("Category", "Unit")]
public class GitHubEventNormalizerTests
{
    private readonly GitHubEventNormalizer _normalizer = new(new ProviderEventSubscriptionRegistry(new IProviderEventSubscription[]
    {
        new GitHubPushEventSubscription(),
        new GitHubPullRequestEventSubscription(),
        new GitHubIssuesEventSubscription()
    }));
    private readonly Guid _repositoryId = Guid.NewGuid();

    [Fact]
    public void Normalize_returns_null_when_event_header_missing()
    {
        var result = _normalizer.Normalize(_repositoryId, "{}", new Dictionary<string, string>());

        result.ShouldBeNull();
    }

    [Fact]
    public void Normalize_returns_null_for_unhandled_event_type()
    {
        var headers = new Dictionary<string, string> { ["X-GitHub-Event"] = "ping" };

        _normalizer.Normalize(_repositoryId, "{\"zen\":\"x\"}", headers).ShouldBeNull();
    }

    [Fact]
    public void Normalize_throws_on_non_json_body()
    {
        // Pins the contract WebhookIngestionService.PublishNormalizedEventAsync relies on: a
        // signed-but-unparseable body surfaces as a catchable JsonException, which the ingestion
        // boundary turns into a 200 + malformed_payload audit instead of a 500. If a refactor made
        // the facade swallow this, the ingestion catch filter would silently stop matching.
        var headers = new Dictionary<string, string> { ["X-GitHub-Event"] = "pull_request" };

        Should.Throw<JsonException>(() => _normalizer.Normalize(_repositoryId, "not json at all", headers));
    }

    [Fact]
    public void Normalize_throws_on_wrong_shape_payload()
    {
        // Valid JSON but missing the pull_request object the normalizer requires → KeyNotFoundException,
        // the other exception type the ingestion boundary catches.
        var headers = new Dictionary<string, string> { ["X-GitHub-Event"] = "pull_request" };

        Should.Throw<KeyNotFoundException>(() => _normalizer.Normalize(_repositoryId, """{"action":"opened"}""", headers));
    }

    [Fact]
    public void Normalize_push_returns_PushReceivedEvent()
    {
        var headers = new Dictionary<string, string>
        {
            ["X-GitHub-Event"] = "push",
            ["X-GitHub-Delivery"] = "delivery-id-abc"
        };
        var body = """
            {
              "ref": "refs/heads/main",
              "before": "before-sha",
              "after": "after-sha",
              "pusher": { "name": "octocat", "email": "octocat@github.com" },
              "sender": { "login": "octocat", "id": 583231 },
              "commits": [
                { "id": "abc", "message": "Update", "author": { "name": "Octo", "email": "o@x" } },
                { "id": "def", "message": "Tweak", "author": { "name": "Cat", "email": "c@x" } }
              ]
            }
            """;

        var result = _normalizer.Normalize(_repositoryId, body, headers).ShouldBeOfType<PushReceivedEvent>();

        result.RepositoryId.ShouldBe(_repositoryId);
        result.ProviderEventId.ShouldBe("delivery-id-abc");
        result.Ref.ShouldBe("refs/heads/main");
        result.BeforeSha.ShouldBe("before-sha");
        result.AfterSha.ShouldBe("after-sha");
        result.PusherExternalId.ShouldBe("583231");
        result.PusherName.ShouldBe("octocat");
        result.Commits.Count.ShouldBe(2);
        result.Commits[0].Sha.ShouldBe("abc");
        result.Commits[1].Sha.ShouldBe("def");
    }

    [Fact]
    public void Normalize_push_generates_delivery_id_when_header_missing()
    {
        var headers = new Dictionary<string, string> { ["X-GitHub-Event"] = "push" };
        var body = """
            {
              "ref": "refs/heads/main", "before": "a", "after": "b",
              "pusher": { "name": "n", "email": "e" }, "sender": { "login": "l", "id": 1 },
              "commits": []
            }
            """;

        var result = _normalizer.Normalize(_repositoryId, body, headers).ShouldBeOfType<PushReceivedEvent>();

        result.ProviderEventId.ShouldNotBeNullOrEmpty();
    }

    [Fact]
    public void Normalize_pull_request_opened_returns_PullRequestOpenedEvent()
    {
        var headers = new Dictionary<string, string> { ["X-GitHub-Event"] = "pull_request" };
        var body = """
            {
              "action": "opened",
              "pull_request": {
                "id": 1234567,
                "number": 42,
                "title": "Add feature",
                "body": "Body text",
                "head": { "ref": "feature/branch", "sha": "headsha" },
                "base": { "ref": "main" },
                "user": { "id": 583231, "login": "octocat" },
                "html_url": "https://github.com/acme/repo/pull/42"
              }
            }
            """;

        var result = _normalizer.Normalize(_repositoryId, body, headers).ShouldBeOfType<PullRequestOpenedEvent>();

        result.ExternalPullRequestId.ShouldBe("1234567");
        result.Number.ShouldBe(42);
        result.Title.ShouldBe("Add feature");
        result.Body.ShouldBe("Body text");
        result.SourceBranch.ShouldBe("feature/branch");
        result.TargetBranch.ShouldBe("main");
        result.AuthorExternalId.ShouldBe("583231");
        result.AuthorName.ShouldBe("octocat");
        result.WebUrl.ShouldBe("https://github.com/acme/repo/pull/42");
    }

    [Fact]
    public void Normalize_pull_request_opened_handles_null_body()
    {
        var headers = new Dictionary<string, string> { ["X-GitHub-Event"] = "pull_request" };
        var body = """
            {
              "action": "opened",
              "pull_request": {
                "id": 1, "number": 1, "title": "x", "body": null,
                "head": { "ref": "f", "sha": "s" }, "base": { "ref": "main" },
                "user": { "id": 1, "login": "u" }, "html_url": "x"
              }
            }
            """;

        var result = _normalizer.Normalize(_repositoryId, body, headers).ShouldBeOfType<PullRequestOpenedEvent>();

        result.Body.ShouldBeNull();
    }

    [Fact]
    public void Normalize_pull_request_synchronize_returns_PullRequestSynchronizedEvent()
    {
        var headers = new Dictionary<string, string> { ["X-GitHub-Event"] = "pull_request" };
        var body = """
            {
              "action": "synchronize",
              "before": "old-sha",
              "after": "new-sha",
              "pull_request": {
                "id": 1234567, "number": 42,
                "head": { "ref": "f", "sha": "new-sha" }, "base": { "ref": "main" },
                "user": { "id": 1, "login": "u" }, "title": "x", "html_url": "x"
              }
            }
            """;

        var result = _normalizer.Normalize(_repositoryId, body, headers).ShouldBeOfType<PullRequestSynchronizedEvent>();

        result.PreviousHeadSha.ShouldBe("old-sha");
        result.NewHeadSha.ShouldBe("new-sha");
        result.Number.ShouldBe(42);
    }

    [Fact]
    public void Normalize_pull_request_closed_merged_returns_PullRequestMergedEvent()
    {
        var headers = new Dictionary<string, string> { ["X-GitHub-Event"] = "pull_request" };
        var body = """
            {
              "action": "closed",
              "pull_request": {
                "id": 1234567, "number": 42, "merged": true, "merge_commit_sha": "merge-sha",
                "head": { "ref": "f" }, "base": { "ref": "main" },
                "user": { "id": 1, "login": "u" }, "title": "x", "html_url": "x"
              },
              "sender": { "id": 888, "login": "merger" }
            }
            """;

        var result = _normalizer.Normalize(_repositoryId, body, headers).ShouldBeOfType<PullRequestMergedEvent>();

        result.MergedByExternalId.ShouldBe("888");
        result.MergedByName.ShouldBe("merger");
        result.MergeCommitSha.ShouldBe("merge-sha");
    }

    [Fact]
    public void Normalize_pull_request_closed_not_merged_returns_PullRequestClosedEvent()
    {
        var headers = new Dictionary<string, string> { ["X-GitHub-Event"] = "pull_request" };
        var body = """
            {
              "action": "closed",
              "pull_request": {
                "id": 1234567, "number": 42, "merged": false,
                "head": { "ref": "f" }, "base": { "ref": "main" },
                "user": { "id": 1, "login": "u" }, "title": "x", "html_url": "x"
              },
              "sender": { "id": 999, "login": "closer" }
            }
            """;

        var result = _normalizer.Normalize(_repositoryId, body, headers).ShouldBeOfType<PullRequestClosedEvent>();

        result.ClosedByExternalId.ShouldBe("999");
        result.ClosedByName.ShouldBe("closer");
    }

    [Fact]
    public void Normalize_pull_request_with_unknown_action_returns_null()
    {
        var headers = new Dictionary<string, string> { ["X-GitHub-Event"] = "pull_request" };
        var body = """
            {
              "action": "edited",
              "pull_request": { "id": 1, "number": 1, "merged": false }
            }
            """;

        _normalizer.Normalize(_repositoryId, body, headers).ShouldBeNull();
    }

    [Fact]
    public void Normalize_issue_opened_returns_IssueOpenedEvent()
    {
        var headers = new Dictionary<string, string> { ["X-GitHub-Event"] = "issues" };
        var body = """
            {
              "action": "opened",
              "issue": {
                "id": 9999, "number": 17, "title": "Bug found",
                "body": "Steps to reproduce...",
                "user": { "id": 100, "login": "reporter" },
                "html_url": "https://github.com/acme/repo/issues/17"
              }
            }
            """;

        var result = _normalizer.Normalize(_repositoryId, body, headers).ShouldBeOfType<IssueOpenedEvent>();

        result.ExternalIssueId.ShouldBe("9999");
        result.Number.ShouldBe(17);
        result.Title.ShouldBe("Bug found");
        result.Body.ShouldBe("Steps to reproduce...");
        result.AuthorExternalId.ShouldBe("100");
        result.AuthorName.ShouldBe("reporter");
    }

    [Fact]
    public void Normalize_issue_closed_returns_IssueClosedEvent()
    {
        var headers = new Dictionary<string, string> { ["X-GitHub-Event"] = "issues" };
        var body = """
            {
              "action": "closed",
              "issue": {
                "id": 9999, "number": 17, "title": "Bug",
                "user": { "id": 1, "login": "r" }, "html_url": "x"
              },
              "sender": { "id": 200, "login": "closer" }
            }
            """;

        var result = _normalizer.Normalize(_repositoryId, body, headers).ShouldBeOfType<IssueClosedEvent>();

        result.ClosedByExternalId.ShouldBe("200");
        result.ClosedByName.ShouldBe("closer");
    }

    [Fact]
    public void Normalize_issue_with_unknown_action_returns_null()
    {
        var headers = new Dictionary<string, string> { ["X-GitHub-Event"] = "issues" };
        var body = """{"action":"edited","issue":{}}""";

        _normalizer.Normalize(_repositoryId, body, headers).ShouldBeNull();
    }

    [Fact]
    public void Normalize_pull_request_opened_extracts_labels()
    {
        var headers = new Dictionary<string, string> { ["X-GitHub-Event"] = "pull_request" };
        var body = """
            {
              "action": "opened",
              "pull_request": {
                "id": 1, "number": 1, "title": "x", "body": null,
                "head": { "ref": "f", "sha": "s" }, "base": { "ref": "main" },
                "user": { "id": 1, "login": "u" }, "html_url": "x",
                "labels": [
                  { "id": 10, "name": "bug", "color": "f29513" },
                  { "id": 11, "name": "needs-review", "color": "5319e7" }
                ]
              }
            }
            """;

        var result = _normalizer.Normalize(_repositoryId, body, headers).ShouldBeOfType<PullRequestOpenedEvent>();

        result.Labels.ShouldBe(new[] { "bug", "needs-review" });
    }

    [Fact]
    public void Normalize_pull_request_opened_returns_empty_labels_when_field_missing()
    {
        // Older GitHub webhook payloads — and any tooling that synthesises minimal fixtures —
        // can ship the pull_request without a labels[] array at all. Surface an empty list,
        // never throw: the matcher's empty-config path must keep working.
        var headers = new Dictionary<string, string> { ["X-GitHub-Event"] = "pull_request" };
        var body = """
            {
              "action": "opened",
              "pull_request": {
                "id": 1, "number": 1, "title": "x", "body": null,
                "head": { "ref": "f", "sha": "s" }, "base": { "ref": "main" },
                "user": { "id": 1, "login": "u" }, "html_url": "x"
              }
            }
            """;

        var result = _normalizer.Normalize(_repositoryId, body, headers).ShouldBeOfType<PullRequestOpenedEvent>();

        result.Labels.ShouldBeEmpty();
    }

    [Fact]
    public void Normalize_pull_request_synchronize_extracts_labels()
    {
        var headers = new Dictionary<string, string> { ["X-GitHub-Event"] = "pull_request" };
        var body = """
            {
              "action": "synchronize",
              "before": "old-sha",
              "after": "new-sha",
              "pull_request": {
                "id": 1, "number": 42,
                "head": { "ref": "f", "sha": "new-sha" }, "base": { "ref": "main" },
                "user": { "id": 1, "login": "u" }, "title": "x", "html_url": "x",
                "labels": [ { "id": 10, "name": "wip" } ]
              }
            }
            """;

        var result = _normalizer.Normalize(_repositoryId, body, headers).ShouldBeOfType<PullRequestSynchronizedEvent>();

        result.Labels.ShouldBe(new[] { "wip" });
    }

    [Fact]
    public void Normalize_pull_request_labels_skips_entries_with_empty_or_null_name()
    {
        // Malformed entries (string-only, null name, missing name) MUST be skipped instead of
        // crashing or producing empty strings — downstream matchers compare label names
        // verbatim and an empty entry would let through every config that doesn't filter on
        // labels (false-positive of "any label matches").
        var headers = new Dictionary<string, string> { ["X-GitHub-Event"] = "pull_request" };
        var body = """
            {
              "action": "opened",
              "pull_request": {
                "id": 1, "number": 1, "title": "x", "body": null,
                "head": { "ref": "f", "sha": "s" }, "base": { "ref": "main" },
                "user": { "id": 1, "login": "u" }, "html_url": "x",
                "labels": [
                  { "id": 10, "name": "ok" },
                  { "id": 11, "name": null },
                  { "id": 12, "name": "" },
                  { "id": 13 },
                  "string-not-object"
                ]
              }
            }
            """;

        var result = _normalizer.Normalize(_repositoryId, body, headers).ShouldBeOfType<PullRequestOpenedEvent>();

        result.Labels.ShouldBe(new[] { "ok" });
    }
}
