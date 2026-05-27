using System.Linq;
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
[Trait("Category", "Unit")]
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

    [Fact]
    public void PrOpened_payload_includes_labels_array()
    {
        // OpenedEvent helper defaults Labels to empty; rebuild here with non-empty labels
        // so we can pin the JSON-level shape AND the value passthrough in one assertion.
        var ev = new PullRequestOpenedEvent
        {
            RepositoryId = RepoA,
            ProviderEventId = "1",
            OccurredAt = DateTimeOffset.UtcNow,
            ExternalPullRequestId = "1",
            Number = 42,
            Title = "x",
            SourceBranch = "f",
            TargetBranch = "main",
            AuthorExternalId = "u",
            AuthorName = "u",
            WebUrl = "x",
            Labels = new[] { "bug", "needs-review" }
        };

        var payload = new PrOpenedMatcher().BuildPayload(ev);

        var labelsEl = payload.GetProperty("labels");
        labelsEl.ValueKind.ShouldBe(JsonValueKind.Array);
        labelsEl.EnumerateArray().Select(l => l.GetString()).ShouldBe(new[] { "bug", "needs-review" });
    }

    [Fact]
    public void PrOpened_payload_labels_default_to_empty_array()
    {
        // The helper's default-constructed event has no labels — the payload must still
        // expose labels as an empty JSON array (not omit / not null), so downstream nodes
        // referencing {{trigger.labels}} get a stable shape regardless of whether the
        // upstream webhook had any labels.
        var payload = new PrOpenedMatcher().BuildPayload(OpenedEvent(RepoA));

        var labelsEl = payload.GetProperty("labels");
        labelsEl.ValueKind.ShouldBe(JsonValueKind.Array);
        labelsEl.GetArrayLength().ShouldBe(0);
    }

    [Fact]
    public void PrUpdated_payload_includes_labels_array()
    {
        var ev = new PullRequestSynchronizedEvent
        {
            RepositoryId = RepoA,
            ProviderEventId = "1",
            OccurredAt = DateTimeOffset.UtcNow,
            ExternalPullRequestId = "1",
            Number = 7,
            PreviousHeadSha = "a",
            NewHeadSha = "b",
            Labels = new[] { "wip" }
        };

        var payload = new PrUpdatedMatcher().BuildPayload(ev);

        var labelsEl = payload.GetProperty("labels");
        labelsEl.ValueKind.ShouldBe(JsonValueKind.Array);
        labelsEl.EnumerateArray().Select(l => l.GetString()).ShouldBe(new[] { "wip" });
    }

    [Fact]
    public void PrUpdated_payload_labels_default_to_empty_array()
    {
        // Mirror of PrOpened_payload_labels_default_to_empty_array — pinning the same
        // empty-array contract on the synchronize-side matcher so downstream node refs to
        // {{trigger.labels}} have a uniform shape regardless of which trigger fired.
        var ev = new PullRequestSynchronizedEvent
        {
            RepositoryId = RepoA,
            ProviderEventId = "1",
            OccurredAt = DateTimeOffset.UtcNow,
            ExternalPullRequestId = "1",
            Number = 7,
            PreviousHeadSha = "a",
            NewHeadSha = "b"
        };

        var payload = new PrUpdatedMatcher().BuildPayload(ev);

        var labelsEl = payload.GetProperty("labels");
        labelsEl.ValueKind.ShouldBe(JsonValueKind.Array);
        labelsEl.GetArrayLength().ShouldBe(0);
    }

    // ─── Schema upgrade: `repositories: [{ repositoryId, labels }]` shape + label AND-filter ────────

    [Fact]
    public void Legacy_labels_AND_filter_matches_when_pr_has_every_required_label()
    {
        // Pre-upgrade, the matcher silently ignored the top-level `labels` array even though
        // the trigger node's ConfigSchema advertised it. PR #23 promotes that to honoured
        // semantics via the shared filter — verify AND semantics on the legacy shape.
        var ev = OpenedWithLabels(RepoA, "bug", "wip", "noisy");
        var config = ParseConfig($$"""{ "repositoryId": "{{RepoA}}", "labels": ["bug", "wip"] }""");

        new PrOpenedMatcher().Match(ev, config).ShouldBeTrue();
    }

    [Fact]
    public void Legacy_labels_AND_filter_rejects_when_pr_is_missing_a_required_label()
    {
        var ev = OpenedWithLabels(RepoA, "bug");
        var config = ParseConfig($$"""{ "repositoryId": "{{RepoA}}", "labels": ["bug", "wip"] }""");

        new PrOpenedMatcher().Match(ev, config).ShouldBeFalse();
    }

    [Fact]
    public void Legacy_empty_labels_array_treats_as_no_label_filter()
    {
        // [] in the legacy shape means "no filter, match any label state" — opposite of the
        // new-shape's empty `repositories: []` which means "no repos, match nothing".
        var ev = OpenedEvent(RepoA);   // no labels
        var config = ParseConfig($$"""{ "repositoryId": "{{RepoA}}", "labels": [] }""");

        new PrOpenedMatcher().Match(ev, config).ShouldBeTrue();
    }

    [Fact]
    public void Legacy_unparseable_repositoryId_preserves_no_filter_behaviour()
    {
        // Existing matcher bug-compat: an invalid Guid in `repositoryId` falls through to
        // "no filter". Pinned so a future tightening is a conscious decision, not silent.
        var ev = OpenedEvent(RepoA);
        var config = ParseConfig("""{ "repositoryId": "not-a-guid" }""");

        new PrOpenedMatcher().Match(ev, config).ShouldBeTrue();
    }

    [Fact]
    public void New_shape_empty_repositories_array_matches_nothing()
    {
        // Operator explicitly configured "no repos" — must NOT silently match-all (that's
        // what `{}` is for). Common scenario: UI clears the picker and saves before adding
        // any entries back.
        var ev = OpenedEvent(RepoA);
        var config = ParseConfig("""{ "repositories": [] }""");

        new PrOpenedMatcher().Match(ev, config).ShouldBeFalse();
    }

    [Fact]
    public void New_shape_entry_with_only_repositoryId_matches_event_from_same_repo()
    {
        var ev = OpenedEvent(RepoA);
        var config = ParseConfig($$"""{ "repositories": [{ "repositoryId": "{{RepoA}}" }] }""");

        new PrOpenedMatcher().Match(ev, config).ShouldBeTrue();
    }

    [Fact]
    public void New_shape_entry_with_only_repositoryId_rejects_event_from_other_repo()
    {
        var ev = OpenedEvent(RepoA);
        var config = ParseConfig($$"""{ "repositories": [{ "repositoryId": "{{RepoB}}" }] }""");

        new PrOpenedMatcher().Match(ev, config).ShouldBeFalse();
    }

    [Fact]
    public void New_shape_entry_with_labels_AND_filter_matches_when_pr_has_every_required_label()
    {
        var ev = OpenedWithLabels(RepoA, "bug", "wip", "extra");
        var config = ParseConfig($$"""{ "repositories": [{ "repositoryId": "{{RepoA}}", "labels": ["bug", "wip"] }] }""");

        new PrOpenedMatcher().Match(ev, config).ShouldBeTrue();
    }

    [Fact]
    public void New_shape_entry_with_labels_AND_filter_rejects_when_one_label_missing()
    {
        var ev = OpenedWithLabels(RepoA, "bug");
        var config = ParseConfig($$"""{ "repositories": [{ "repositoryId": "{{RepoA}}", "labels": ["bug", "wip"] }] }""");

        new PrOpenedMatcher().Match(ev, config).ShouldBeFalse();
    }

    [Fact]
    public void New_shape_entry_with_empty_labels_array_skips_label_filter()
    {
        // Per-entry semantics mirror the legacy top-level: empty labels = no filter (for
        // this entry).
        var ev = OpenedEvent(RepoA);
        var config = ParseConfig($$"""{ "repositories": [{ "repositoryId": "{{RepoA}}", "labels": [] }] }""");

        new PrOpenedMatcher().Match(ev, config).ShouldBeTrue();
    }

    [Fact]
    public void New_shape_multiple_entries_fires_when_any_entry_matches()
    {
        // OR-across-entries, AND-within-entry. Operator can express "match repo X with
        // ['a','b'] OR repo Y with ['c']" in one activation row.
        var ev = OpenedWithLabels(RepoA, "b");
        var config = ParseConfig($$"""
            {
              "repositories": [
                { "repositoryId": "{{RepoA}}", "labels": ["a"] },
                { "repositoryId": "{{RepoA}}", "labels": ["b"] }
              ]
            }
            """);

        new PrOpenedMatcher().Match(ev, config).ShouldBeTrue();
    }

    [Fact]
    public void New_shape_multiple_entries_returns_false_when_no_entry_matches()
    {
        var ev = OpenedWithLabels(RepoA, "x");
        var config = ParseConfig($$"""
            {
              "repositories": [
                { "repositoryId": "{{RepoA}}", "labels": ["a"] },
                { "repositoryId": "{{RepoB}}", "labels": ["x"] }
              ]
            }
            """);

        new PrOpenedMatcher().Match(ev, config).ShouldBeFalse();
    }

    [Fact]
    public void New_shape_non_array_value_returns_false()
    {
        // A clearly-broken config (string where array expected) must NOT silently match-all.
        // Returning false here is conservative: the operator was using the new shape, just
        // typed it wrong; reject the row rather than let the workflow fire on every PR.
        var ev = OpenedEvent(RepoA);
        var config = ParseConfig("""{ "repositories": "not-an-array" }""");

        new PrOpenedMatcher().Match(ev, config).ShouldBeFalse();
    }

    [Fact]
    public void New_shape_entry_with_unparseable_repositoryId_is_skipped()
    {
        // One bad entry shouldn't poison the whole config — other entries still get a chance.
        var ev = OpenedEvent(RepoA);
        var config = ParseConfig($$"""
            {
              "repositories": [
                { "repositoryId": "not-a-guid" },
                { "repositoryId": "{{RepoA}}" }
              ]
            }
            """);

        new PrOpenedMatcher().Match(ev, config).ShouldBeTrue();
    }

    [Fact]
    public void New_shape_takes_precedence_when_both_new_and_legacy_keys_present()
    {
        // If the config carries both `repositories[]` (new) and `repositoryId` (legacy), the
        // new shape wins — single source of truth, no ambiguous "fallback merge".
        // Verified by: new shape has an empty array → no match, even though the legacy
        // `repositoryId` matches the event's repo.
        var ev = OpenedEvent(RepoA);
        var config = ParseConfig($$"""{ "repositories": [], "repositoryId": "{{RepoA}}" }""");

        new PrOpenedMatcher().Match(ev, config).ShouldBeFalse();
    }

    [Fact]
    public void PrUpdated_honours_new_repositories_shape()
    {
        // The shared filter is plumbed into both matchers — parity check.
        var ev = new PullRequestSynchronizedEvent
        {
            RepositoryId = RepoA,
            ProviderEventId = "1",
            OccurredAt = DateTimeOffset.UtcNow,
            ExternalPullRequestId = "1",
            Number = 7,
            PreviousHeadSha = "a",
            NewHeadSha = "b",
            Labels = new[] { "ship-it" }
        };
        var config = ParseConfig($$"""{ "repositories": [{ "repositoryId": "{{RepoA}}", "labels": ["ship-it"] }] }""");

        new PrUpdatedMatcher().Match(ev, config).ShouldBeTrue();
    }

    [Fact]
    public void PrUpdated_honours_legacy_labels_AND_filter()
    {
        var ev = new PullRequestSynchronizedEvent
        {
            RepositoryId = RepoA,
            ProviderEventId = "1",
            OccurredAt = DateTimeOffset.UtcNow,
            ExternalPullRequestId = "1",
            Number = 7,
            PreviousHeadSha = "a",
            NewHeadSha = "b",
            Labels = new[] { "bug" }
        };
        var config = ParseConfig($$"""{ "repositoryId": "{{RepoA}}", "labels": ["bug", "wip"] }""");

        new PrUpdatedMatcher().Match(ev, config).ShouldBeFalse();
    }

    // ─── Defensive parsing — malformed JSON shapes the validator might miss ────────

    [Fact]
    public void Legacy_labels_filter_applied_even_when_repositoryId_absent()
    {
        // A legacy config that only sets `labels` (no `repositoryId` key at all) still
        // applies the label filter — semantically "match any repo, but only PRs with these
        // labels". Less common than the both-present form but the parser supports it.
        var ev = OpenedWithLabels(RepoA, "shipping");
        var config = ParseConfig("""{ "labels": ["shipping"] }""");

        new PrOpenedMatcher().Match(ev, config).ShouldBeTrue();
    }

    [Fact]
    public void Legacy_non_array_labels_value_treats_as_no_filter()
    {
        // Half-typed config — `labels` is a string, not an array. The parser SHOULD fall
        // back to "no label filter" rather than reject the match: the operator's intent
        // was clearly to filter, but the config is malformed; failing closed (no match)
        // would silently break workflows. Failing open (no filter) lets the workflow fire
        // and surfaces the misconfiguration in the run log.
        var ev = OpenedEvent(RepoA);
        var config = ParseConfig($$"""{ "repositoryId": "{{RepoA}}", "labels": "not-an-array" }""");

        new PrOpenedMatcher().Match(ev, config).ShouldBeTrue();
    }

    [Fact]
    public void New_shape_entry_with_non_string_repositoryId_is_skipped()
    {
        // The trigger node ConfigSchema enforces `type: string` on `repositoryId`, but the
        // matcher MUST tolerate a stored config that bypassed validation (e.g. a future
        // schema relaxation, or a direct DB edit). Defensive skip — sibling entries still
        // get a chance.
        var ev = OpenedEvent(RepoA);
        var config = ParseConfig($$"""
            {
              "repositories": [
                { "repositoryId": 12345 },
                { "repositoryId": "{{RepoA}}" }
              ]
            }
            """);

        new PrOpenedMatcher().Match(ev, config).ShouldBeTrue();
    }

    [Fact]
    public void New_shape_non_object_entry_is_skipped()
    {
        // The schema declares `items: { type: object }`, but again the matcher is
        // defensive against stored configs that didn't go through validation. A string-
        // valued entry in the array MUST be skipped, not crash the matcher.
        var ev = OpenedEvent(RepoA);
        var config = ParseConfig($$"""
            {
              "repositories": [
                "not-an-object",
                { "repositoryId": "{{RepoA}}" }
              ]
            }
            """);

        new PrOpenedMatcher().Match(ev, config).ShouldBeTrue();
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

    private static PullRequestOpenedEvent OpenedWithLabels(Guid repositoryId, params string[] labels) => new()
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
        WebUrl = "https://example/1",
        Labels = labels
    };

    private static JsonElement ParseConfig(string json) => JsonDocument.Parse(json).RootElement;

    private static JsonElement EmptyConfig() => JsonDocument.Parse("{}").RootElement;
}
