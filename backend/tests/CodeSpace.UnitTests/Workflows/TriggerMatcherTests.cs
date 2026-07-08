using System.Text.Json;
using CodeSpace.Core.Services.Workflows.Nodes.Builtin;
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
    public void PrMerged_typekey_pinned()
    {
        new PrMergedMatcher().TypeKey.ShouldBe("trigger.pr.merged");
    }

    [Fact]
    public void PrMerged_matches_merged_event_with_empty_config()
    {
        new PrMergedMatcher().Match(MergedEvent(RepoA), EmptyConfig()).ShouldBeTrue();
    }

    [Fact]
    public void PrMerged_rejects_opened_event()
    {
        // A matcher must reject events of the wrong TYPE — the dispatcher relies on this for
        // its empty-config candidate probe (only the right matcher claims the event).
        new PrMergedMatcher().Match(OpenedEvent(RepoA), EmptyConfig()).ShouldBeFalse();
    }

    [Fact]
    public void PrMerged_repository_filter_matches_same_repo()
    {
        var config = JsonDocument.Parse($"{{ \"repositoryId\": \"{RepoA}\" }}").RootElement;
        new PrMergedMatcher().Match(MergedEvent(RepoA), config).ShouldBeTrue();
    }

    [Fact]
    public void PrMerged_repository_filter_excludes_other_repos()
    {
        var config = JsonDocument.Parse($"{{ \"repositoryId\": \"{RepoB}\" }}").RootElement;
        new PrMergedMatcher().Match(MergedEvent(RepoA), config).ShouldBeFalse();
    }

    [Fact]
    public void PrMerged_payload_contains_expected_fields()
    {
        var payload = new PrMergedMatcher().BuildPayload(MergedEvent(RepoA));

        payload.GetProperty("repositoryId").GetString().ShouldBe(RepoA.ToString());
        payload.GetProperty("number").GetInt32().ShouldBe(99);
        payload.GetProperty("mergedByName").GetString().ShouldBe("bob");
        payload.GetProperty("mergeCommitSha").GetString().ShouldBe("abc123");
    }

    [Fact]
    public void PrMerged_payload_includes_labels_array()
    {
        var ev = new PullRequestMergedEvent
        {
            RepositoryId = RepoA,
            ProviderEventId = "1",
            OccurredAt = DateTimeOffset.UtcNow,
            ExternalPullRequestId = "1",
            Number = 99,
            MergedByExternalId = "u",
            MergedByName = "bob",
            Labels = new[] { "release" }
        };

        var labelsEl = new PrMergedMatcher().BuildPayload(ev).GetProperty("labels");
        labelsEl.ValueKind.ShouldBe(JsonValueKind.Array);
        labelsEl.EnumerateArray().Select(l => l.GetString()).ShouldBe(new[] { "release" });
    }

    [Fact]
    public void PrMerged_payload_labels_default_to_empty_array()
    {
        var labelsEl = new PrMergedMatcher().BuildPayload(MergedEvent(RepoA)).GetProperty("labels");
        labelsEl.ValueKind.ShouldBe(JsonValueKind.Array);
        labelsEl.GetArrayLength().ShouldBe(0);
    }

    [Fact]
    public void PrMerged_honours_new_repositories_shape_with_labels_AND_filter()
    {
        // The merged matcher delegates to the same shared filter — parity check that the
        // repo + AND-label semantics apply identically (e.g. "deploy when a PR labelled
        // 'release' AND 'approved' merges").
        var ev = MergedWithLabels(RepoA, "release", "approved", "extra");
        var config = ParseConfig($$"""{ "repositories": [{ "repositoryId": "{{RepoA}}", "labels": ["release", "approved"] }] }""");

        new PrMergedMatcher().Match(ev, config).ShouldBeTrue();
    }

    [Fact]
    public void PrMerged_new_shape_rejects_when_a_required_label_is_missing()
    {
        var ev = MergedWithLabels(RepoA, "release");
        var config = ParseConfig($$"""{ "repositories": [{ "repositoryId": "{{RepoA}}", "labels": ["release", "approved"] }] }""");

        new PrMergedMatcher().Match(ev, config).ShouldBeFalse();
    }

    [Fact]
    public void PrMerged_OutputSchema_lists_exactly_the_keys_BuildPayload_emits()
    {
        var payload = new PrMergedMatcher().BuildPayload(MergedWithLabels(RepoA, "release"));

        var payloadKeys = payload.EnumerateObject().Select(p => p.Name).OrderBy(s => s).ToList();
        var schemaKeys = ExtractSchemaPropertyNames(new TriggerPrMergedNode().Manifest.OutputSchema).OrderBy(s => s).ToList();

        payloadKeys.ShouldBe(schemaKeys,
            customMessage:
                "TriggerPrMergedNode.OutputSchema is out of sync with PrMergedMatcher.BuildPayload. " +
                "See PrOpened_OutputSchema_lists_exactly_the_keys_BuildPayload_emits for the failure mode and fix recipe.");
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

    [Fact]
    public void PrOpened_payload_surfaces_isDraft()
    {
        // A draft PR → isDraft:true; a non-draft (the default helper) → isDraft:false, never
        // omitted. Lets a workflow gate downstream nodes on {{trigger.isDraft}} (e.g. skip AI
        // review while the PR is still a draft).
        var draft = new PullRequestOpenedEvent
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
            IsDraft = true
        };

        new PrOpenedMatcher().BuildPayload(draft).GetProperty("isDraft").GetBoolean().ShouldBeTrue();
        new PrOpenedMatcher().BuildPayload(OpenedEvent(RepoA)).GetProperty("isDraft").GetBoolean().ShouldBeFalse();
    }

    [Fact]
    public void PrUpdated_payload_surfaces_isDraft()
    {
        // Mirror of PrOpened — the synchronize-side payload exposes the same isDraft flag so a
        // workflow targeting either trigger reads {{trigger.isDraft}} with a uniform shape.
        var draft = new PullRequestSynchronizedEvent
        {
            RepositoryId = RepoA,
            ProviderEventId = "1",
            OccurredAt = DateTimeOffset.UtcNow,
            ExternalPullRequestId = "1",
            Number = 7,
            PreviousHeadSha = "a",
            NewHeadSha = "b",
            IsDraft = true
        };
        var ready = new PullRequestSynchronizedEvent
        {
            RepositoryId = RepoA,
            ProviderEventId = "1",
            OccurredAt = DateTimeOffset.UtcNow,
            ExternalPullRequestId = "1",
            Number = 7,
            PreviousHeadSha = "a",
            NewHeadSha = "b"
        };

        new PrUpdatedMatcher().BuildPayload(draft).GetProperty("isDraft").GetBoolean().ShouldBeTrue();
        new PrUpdatedMatcher().BuildPayload(ready).GetProperty("isDraft").GetBoolean().ShouldBeFalse();
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

    // ─── Drift detectors: OutputSchema ↔ BuildPayload contract ──────────────────────
    // Two independent sources of truth ship side by side: the trigger node declares
    // an OutputSchema (read by the inspector's {{trigger.*}} autocomplete), and the
    // matcher's BuildPayload writes the actual runtime payload. If they drift, the
    // inspector either lies about available fields (autocomplete shows entries that
    // resolve to undefined at run time) or silently hides real fields the operator
    // could reference. PR #22 introduced exactly this drift for `labels`; these
    // tests fail loudly on the next slip.

    [Fact]
    public void PrOpened_OutputSchema_lists_exactly_the_keys_BuildPayload_emits()
    {
        var matcher = new PrOpenedMatcher();
        var ev = OpenedWithLabels(RepoA, "bug");
        var payload = matcher.BuildPayload(ev);

        var payloadKeys = payload.EnumerateObject().Select(p => p.Name).OrderBy(s => s).ToList();
        var schemaKeys = ExtractSchemaPropertyNames(new TriggerPrOpenedNode().Manifest.OutputSchema).OrderBy(s => s).ToList();

        payloadKeys.ShouldBe(schemaKeys,
            customMessage:
                "TriggerPrOpenedNode.OutputSchema is out of sync with PrOpenedMatcher.BuildPayload. " +
                "The inspector's {{trigger.*}} autocomplete reads OutputSchema; the engine evaluates {{trigger.*}} " +
                "against BuildPayload at run time. A mismatch shows ghosts in autocomplete (resolve to undefined) " +
                "or hides real fields the operator could reference. " +
                "Fix: add or drop properties in TriggerPrOpenedNode.OutputSchema until this list equals the payload keys.");
    }

    [Fact]
    public void PrUpdated_OutputSchema_lists_exactly_the_keys_BuildPayload_emits()
    {
        var matcher = new PrUpdatedMatcher();
        var ev = new PullRequestSynchronizedEvent
        {
            RepositoryId = RepoA,
            ProviderEventId = "1",
            OccurredAt = DateTimeOffset.UtcNow,
            ExternalPullRequestId = "1",
            Number = 42,
            PreviousHeadSha = "a",
            NewHeadSha = "b",
            Labels = new[] { "bug" }
        };
        var payload = matcher.BuildPayload(ev);

        var payloadKeys = payload.EnumerateObject().Select(p => p.Name).OrderBy(s => s).ToList();
        var schemaKeys = ExtractSchemaPropertyNames(new TriggerPrUpdatedNode().Manifest.OutputSchema).OrderBy(s => s).ToList();

        payloadKeys.ShouldBe(schemaKeys,
            customMessage:
                "TriggerPrUpdatedNode.OutputSchema is out of sync with PrUpdatedMatcher.BuildPayload. " +
                "See PrOpened_OutputSchema_lists_exactly_the_keys_BuildPayload_emits for the failure mode and fix recipe.");
    }

    /// <summary>
    /// Pulls the top-level <c>properties</c> object's keys from a JSON Schema, the
    /// same surface SchemaForm walks to render fields. Tolerates missing /
    /// non-object schemas by returning an empty list — keeps the diff-vs-payload
    /// comparison readable instead of throwing in setup.
    /// </summary>
    private static IEnumerable<string> ExtractSchemaPropertyNames(JsonElement schema)
    {
        if (schema.ValueKind != JsonValueKind.Object) return Array.Empty<string>();
        if (!schema.TryGetProperty("properties", out var props)) return Array.Empty<string>();
        if (props.ValueKind != JsonValueKind.Object) return Array.Empty<string>();
        return props.EnumerateObject().Select(p => p.Name).ToList();
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

    private static PullRequestMergedEvent MergedEvent(Guid repositoryId) => new()
    {
        RepositoryId = repositoryId,
        ProviderEventId = "1",
        OccurredAt = DateTimeOffset.UtcNow,
        ExternalPullRequestId = "1",
        Number = 99,
        MergedByExternalId = "u2",
        MergedByName = "bob",
        MergeCommitSha = "abc123"
    };

    private static PullRequestMergedEvent MergedWithLabels(Guid repositoryId, params string[] labels) => new()
    {
        RepositoryId = repositoryId,
        ProviderEventId = "1",
        OccurredAt = DateTimeOffset.UtcNow,
        ExternalPullRequestId = "1",
        Number = 99,
        MergedByExternalId = "u2",
        MergedByName = "bob",
        MergeCommitSha = "abc123",
        Labels = labels
    };

    private static JsonElement ParseConfig(string json) => JsonDocument.Parse(json).RootElement;

    private static JsonElement EmptyConfig() => JsonDocument.Parse("{}").RootElement;
}
