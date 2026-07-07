using System.Text.Json;
using CodeSpace.Core.Services.Workflows.Nodes.Builtin;
using CodeSpace.Core.Services.Workflows.RunSources.Matchers;
using CodeSpace.Messages.Events.Push;
using CodeSpace.Messages.Events.PullRequest;
using Shouldly;

namespace CodeSpace.UnitTests.Workflows;

/// <summary>
/// Matcher tests for <see cref="PushMatcher"/> / <see cref="PushTriggerMatcherFilter"/>. Push uses a
/// different filter axis from the PR triggers (repository + branch, no labels), so it gets its own
/// suite. Covers the type guard, the repository filter, the branch OR-filter + ref normalization,
/// the combined AND of both axes, the payload shape, and the OutputSchema ↔ BuildPayload drift pin.
/// </summary>
[Trait("Category", "Unit")]
public class PushMatcherTests
{
    private static readonly Guid RepoA = Guid.NewGuid();
    private static readonly Guid RepoB = Guid.NewGuid();

    [Fact]
    public void Push_typekey_pinned()
    {
        new PushMatcher().TypeKey.ShouldBe("trigger.push");
    }

    [Fact]
    public void Matches_push_event_with_empty_config()
    {
        new PushMatcher().Match(PushEvent(RepoA, "refs/heads/main"), Empty()).ShouldBeTrue();
    }

    [Fact]
    public void Rejects_non_push_event()
    {
        // Wrong event TYPE → false, so the dispatcher's empty-config probe never mis-claims it.
        var opened = new PullRequestOpenedEvent
        {
            RepositoryId = RepoA, ProviderEventId = "1", OccurredAt = DateTimeOffset.UtcNow,
            ExternalPullRequestId = "1", Number = 1, Title = "x", SourceBranch = "f", TargetBranch = "main",
            AuthorExternalId = "u", AuthorName = "u", WebUrl = "x"
        };

        new PushMatcher().Match(opened, Empty()).ShouldBeFalse();
    }

    [Fact]
    public void Repository_filter_matches_same_repo()
    {
        var config = Parse($$"""{ "repositoryId": "{{RepoA}}" }""");
        new PushMatcher().Match(PushEvent(RepoA, "refs/heads/main"), config).ShouldBeTrue();
    }

    [Fact]
    public void Repository_filter_excludes_other_repo()
    {
        var config = Parse($$"""{ "repositoryId": "{{RepoB}}" }""");
        new PushMatcher().Match(PushEvent(RepoA, "refs/heads/main"), config).ShouldBeFalse();
    }

    [Fact]
    public void Repository_filter_unparseable_id_treated_as_no_filter()
    {
        // Mirror of the PR filter's tolerance: a stored config that bypassed validation must not
        // crash or silently block — fall through to "no repo filter".
        var config = Parse("""{ "repositoryId": "not-a-guid" }""");
        new PushMatcher().Match(PushEvent(RepoA, "refs/heads/main"), config).ShouldBeTrue();
    }

    [Fact]
    public void Branch_filter_matches_when_event_branch_is_listed()
    {
        // Config carries short names; the event ref is fully-qualified — the filter normalizes both.
        var config = Parse("""{ "branches": ["main", "develop"] }""");
        new PushMatcher().Match(PushEvent(RepoA, "refs/heads/develop"), config).ShouldBeTrue();
    }

    [Fact]
    public void Branch_filter_excludes_when_event_branch_not_listed()
    {
        var config = Parse("""{ "branches": ["main", "develop"] }""");
        new PushMatcher().Match(PushEvent(RepoA, "refs/heads/feature/x"), config).ShouldBeFalse();
    }

    [Fact]
    public void Branch_filter_empty_array_matches_any_branch()
    {
        var config = Parse("""{ "branches": [] }""");
        new PushMatcher().Match(PushEvent(RepoA, "refs/heads/anything"), config).ShouldBeTrue();
    }

    [Fact]
    public void Branch_filter_does_not_match_a_tag_push()
    {
        // A branch filter must never fire on a tag push — the tag ref keeps its refs/tags/ prefix,
        // which won't equal any short branch name.
        var config = Parse("""{ "branches": ["main"] }""");
        new PushMatcher().Match(PushEvent(RepoA, "refs/tags/v1.0.0"), config).ShouldBeFalse();
    }

    [Fact]
    public void Repository_and_branch_filters_AND_together()
    {
        var config = Parse($$"""{ "repositoryId": "{{RepoA}}", "branches": ["main"] }""");

        new PushMatcher().Match(PushEvent(RepoA, "refs/heads/main"), config).ShouldBeTrue();
        new PushMatcher().Match(PushEvent(RepoA, "refs/heads/dev"), config).ShouldBeFalse("right repo, wrong branch");
        new PushMatcher().Match(PushEvent(RepoB, "refs/heads/main"), config).ShouldBeFalse("right branch, wrong repo");
    }

    [Fact]
    public void Payload_contains_expected_fields()
    {
        var payload = new PushMatcher().BuildPayload(PushEvent(RepoA, "refs/heads/main"));

        payload.GetProperty("repositoryId").GetString().ShouldBe(RepoA.ToString());
        payload.GetProperty("ref").GetString().ShouldBe("refs/heads/main");
        payload.GetProperty("branch").GetString().ShouldBe("main", "short branch is derived from the ref for the {{trigger.branch}} convenience field");
        payload.GetProperty("beforeSha").GetString().ShouldBe("before");
        payload.GetProperty("afterSha").GetString().ShouldBe("after");
        payload.GetProperty("pusherName").GetString().ShouldBe("alice");
        payload.GetProperty("commitCount").GetInt32().ShouldBe(2);
    }

    [Fact]
    public void OutputSchema_lists_exactly_the_keys_BuildPayload_emits()
    {
        var payload = new PushMatcher().BuildPayload(PushEvent(RepoA, "refs/heads/main"));

        var payloadKeys = payload.EnumerateObject().Select(p => p.Name).OrderBy(s => s).ToList();
        var schemaKeys = ExtractSchemaPropertyNames(new TriggerPushNode().Manifest.OutputSchema).OrderBy(s => s).ToList();

        payloadKeys.ShouldBe(schemaKeys,
            customMessage:
                "TriggerPushNode.OutputSchema is out of sync with PushMatcher.BuildPayload. The inspector's " +
                "{{trigger.*}} autocomplete reads OutputSchema; the engine evaluates {{trigger.*}} against " +
                "BuildPayload at run time. Fix: align TriggerPushNode.OutputSchema with the payload keys.");
    }

    private static IEnumerable<string> ExtractSchemaPropertyNames(JsonElement schema)
    {
        if (schema.ValueKind != JsonValueKind.Object) return Array.Empty<string>();
        if (!schema.TryGetProperty("properties", out var props)) return Array.Empty<string>();
        if (props.ValueKind != JsonValueKind.Object) return Array.Empty<string>();
        return props.EnumerateObject().Select(p => p.Name).ToList();
    }

    private static PushReceivedEvent PushEvent(Guid repositoryId, string gitRef) => new()
    {
        RepositoryId = repositoryId,
        ProviderEventId = "1",
        OccurredAt = DateTimeOffset.UtcNow,
        Ref = gitRef,
        BeforeSha = "before",
        AfterSha = "after",
        PusherExternalId = "u1",
        PusherName = "alice",
        Commits = new[]
        {
            new CommitSummary { Sha = "c1", Message = "m1", AuthorEmail = "a@x", AuthorName = "alice" },
            new CommitSummary { Sha = "c2", Message = "m2", AuthorEmail = "a@x", AuthorName = "alice" },
        }
    };

    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement;
    private static JsonElement Empty() => JsonDocument.Parse("{}").RootElement;
}
