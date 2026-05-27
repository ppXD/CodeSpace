using System.Text.Json;

namespace CodeSpace.Core.Services.Workflows.RunSources.Matchers;

/// <summary>
/// Shared filter logic for PR-triggered matchers. Both <see cref="PrOpenedMatcher"/> and
/// <see cref="PrUpdatedMatcher"/> answer the same question — "does this event's repository
/// + labels satisfy the activation config?" — and the config schema is identical (a list of
/// per-repo label constraints). Pulled out so the schema-vs-matcher contract lives in one
/// place and a future PR-merged / PR-closed trigger can plug in with one line.
///
/// <para>Config schema (current shape):</para>
/// <code>
/// {
///   "repositories": [
///     { "repositoryId": "&lt;uuid&gt;", "labels": ["bug", "wip"] }
///   ]
/// }
/// </code>
///
/// <para>Match precedence:</para>
/// <list type="number">
///   <item><c>repositories</c> present + valid array → iterate; entry matches if the
///         repositoryId matches AND every label listed is present on the event (AND
///         semantics — see <see cref="EntryMatches"/>). Any entry-match → true.</item>
///   <item><c>repositories</c> present but not an array → false (clearly broken config;
///         silent match-all would let workflows fire unexpectedly).</item>
///   <item>Legacy <c>repositoryId</c> at the top level → check that single repo + optional
///         top-level <c>labels</c> AND-filter. Honoured even though the previous matcher
///         silently ignored <c>labels</c>: the schema has advertised the field since the
///         trigger node landed, and "ignored → honoured" is non-breaking under the no-prod-
///         deployments invariant.</item>
///   <item>Empty config <c>{}</c> → true (no filter).</item>
///   <item>Legacy <c>repositoryId</c> not parseable as a Guid → true (preserves the pre-PR
///         no-filter behaviour for malformed configs).</item>
/// </list>
/// </summary>
internal static class PrTriggerMatcherFilter
{
    public static bool Matches(JsonElement activationConfig, Guid eventRepositoryId, IReadOnlyList<string> eventLabels)
    {
        if (activationConfig.ValueKind != JsonValueKind.Object) return true;

        if (activationConfig.TryGetProperty("repositories", out var reposEl)) return MatchesNewShape(reposEl, eventRepositoryId, eventLabels);

        return MatchesLegacyShape(activationConfig, eventRepositoryId, eventLabels);
    }

    /// <summary>
    /// New shape: <c>repositories</c> is an array of <c>{ repositoryId, labels }</c> entries.
    /// Any entry that matches both the repo and the (optional) AND-label filter fires the
    /// workflow. An empty array deliberately matches NOTHING — the operator explicitly
    /// configured "no repos", which should not silently match-all.
    /// </summary>
    private static bool MatchesNewShape(JsonElement reposEl, Guid eventRepositoryId, IReadOnlyList<string> eventLabels)
    {
        if (reposEl.ValueKind != JsonValueKind.Array) return false;

        foreach (var entry in reposEl.EnumerateArray())
        {
            if (EntryMatches(entry, eventRepositoryId, eventLabels)) return true;
        }

        return false;
    }

    private static bool EntryMatches(JsonElement entry, Guid eventRepositoryId, IReadOnlyList<string> eventLabels)
    {
        if (entry.ValueKind != JsonValueKind.Object) return false;
        if (!entry.TryGetProperty("repositoryId", out var repoIdEl)) return false;
        if (repoIdEl.ValueKind != JsonValueKind.String) return false;
        if (!Guid.TryParse(repoIdEl.GetString(), out var configuredRepoId)) return false;
        if (configuredRepoId != eventRepositoryId) return false;

        return AndLabelsFilterMatches(entry, eventLabels);
    }

    /// <summary>
    /// Legacy shape: top-level <c>repositoryId</c> string + optional top-level <c>labels</c>
    /// array. The labels filter applies AND semantics, identical to the new shape's per-entry
    /// filter, so the user-visible matching contract is uniform regardless of which shape the
    /// config uses.
    /// </summary>
    private static bool MatchesLegacyShape(JsonElement activationConfig, Guid eventRepositoryId, IReadOnlyList<string> eventLabels)
    {
        if (!activationConfig.TryGetProperty("repositoryId", out var repoIdEl)) return AndLabelsFilterMatches(activationConfig, eventLabels);
        if (repoIdEl.ValueKind == JsonValueKind.Null) return AndLabelsFilterMatches(activationConfig, eventLabels);
        if (repoIdEl.ValueKind != JsonValueKind.String) return true;
        if (!Guid.TryParse(repoIdEl.GetString(), out var configuredRepoId)) return true;
        if (configuredRepoId != eventRepositoryId) return false;

        return AndLabelsFilterMatches(activationConfig, eventLabels);
    }

    /// <summary>
    /// AND-filter: every label listed in <c>labels</c> (case-sensitive ordinal compare, per
    /// provider convention) MUST be present on the event. An empty list or missing key means
    /// "no label filter" → always true. Non-array <c>labels</c> is treated as no filter so a
    /// half-typed config doesn't silently block matches that the operator clearly wanted.
    /// </summary>
    private static bool AndLabelsFilterMatches(JsonElement scope, IReadOnlyList<string> eventLabels)
    {
        if (!scope.TryGetProperty("labels", out var labelsEl)) return true;
        if (labelsEl.ValueKind != JsonValueKind.Array) return true;
        if (labelsEl.GetArrayLength() == 0) return true;

        foreach (var required in labelsEl.EnumerateArray())
        {
            if (required.ValueKind != JsonValueKind.String) continue;

            var name = required.GetString();
            if (string.IsNullOrEmpty(name)) continue;

            if (!eventLabels.Contains(name, StringComparer.Ordinal)) return false;
        }

        return true;
    }
}
