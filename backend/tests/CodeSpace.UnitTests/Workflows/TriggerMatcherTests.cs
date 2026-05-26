using System.Text.Json;
using CodeSpace.Core.Services.Workflows.RunSources.Matchers;
using CodeSpace.Messages.Events.PullRequest;
using Shouldly;

namespace CodeSpace.UnitTests.Workflows;

/// <summary>
/// Matcher tests for the run-source dispatcher. Matchers decide whether an incoming
/// NormalizedEvent fires a <c>workflow_activation</c> row. Their TypeKey is the dispatch
/// key — pinning protects the activation rows that reference it as a string. Their config
/// schema MUST tolerate an empty {} config (treated as "no repository filter") because the
/// dispatcher uses an empty probe to decide if the matcher is a candidate at all.
/// </summary>
public class TriggerMatcherTests
{
    private static readonly Guid RepoA = Guid.NewGuid();
    private static readonly Guid RepoB = Guid.NewGuid();

    [Fact]
    public void PrOpened_typekey_pinned()
    {
        new PrOpenedMatcher().TypeKey.ShouldBe("trigger.pr.opened");
    }

    [Fact]
    public void PrUpdated_typekey_pinned()
    {
        new PrUpdatedMatcher().TypeKey.ShouldBe("trigger.pr.updated");
    }

    [Fact]
    public void PrOpened_matches_open_event_with_empty_config()
    {
        var ev = OpenedEvent(RepoA);
        new PrOpenedMatcher().Match(ev, EmptyConfig()).ShouldBeTrue();
    }

    [Fact]
    public void PrOpened_rejects_synchronize_event()
    {
        var ev = new PullRequestSynchronizedEvent
        {
            RepositoryId = RepoA,
            ProviderEventId = "1",
            OccurredAt = DateTimeOffset.UtcNow,
            ExternalPullRequestId = "1",
            Number = 1,
            PreviousHeadSha = "a",
            NewHeadSha = "b"
        };

        new PrOpenedMatcher().Match(ev, EmptyConfig()).ShouldBeFalse();
    }

    [Fact]
    public void PrOpened_repository_filter_excludes_other_repos()
    {
        var matcher = new PrOpenedMatcher();
        var ev = OpenedEvent(RepoA);
        var config = JsonDocument.Parse($"{{ \"repositoryId\": \"{RepoB}\" }}").RootElement;

        matcher.Match(ev, config).ShouldBeFalse();
    }

    [Fact]
    public void PrOpened_repository_filter_matches_same_repo()
    {
        var matcher = new PrOpenedMatcher();
        var ev = OpenedEvent(RepoA);
        var config = JsonDocument.Parse($"{{ \"repositoryId\": \"{RepoA}\" }}").RootElement;

        matcher.Match(ev, config).ShouldBeTrue();
    }

    [Fact]
    public void PrOpened_payload_contains_expected_fields()
    {
        var matcher = new PrOpenedMatcher();
        var ev = OpenedEvent(RepoA);
        var payload = matcher.BuildPayload(ev);

        payload.GetProperty("number").GetInt32().ShouldBe(42);
        payload.GetProperty("title").GetString().ShouldBe("Fix bug");
        payload.GetProperty("repositoryId").GetString().ShouldBe(RepoA.ToString());
    }

    private static PullRequestOpenedEvent OpenedEvent(Guid repositoryId) => new()
    {
        RepositoryId = repositoryId,
        ProviderEventId = "1",
        OccurredAt = DateTimeOffset.UtcNow,
        ExternalPullRequestId = "1",
        Number = 42,
        Title = "Fix bug",
        Body = "body",
        SourceBranch = "feature",
        TargetBranch = "main",
        AuthorExternalId = "u1",
        AuthorName = "alice",
        WebUrl = "https://example/1"
    };

    private static JsonElement EmptyConfig() => JsonDocument.Parse("{}").RootElement;
}
