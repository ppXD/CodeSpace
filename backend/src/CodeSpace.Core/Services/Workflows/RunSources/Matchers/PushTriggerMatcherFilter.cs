using System.Text.Json;

namespace CodeSpace.Core.Services.Workflows.RunSources.Matchers;

/// <summary>
/// Shared filter logic for the push trigger. A push event has no labels (unlike the PR
/// triggers) — its discriminating axes are the repository and the branch the push landed on.
/// So this is a separate filter from <see cref="PrTriggerMatcherFilter"/> rather than a reuse:
/// the per-PR AND-label semantics don't apply, and push instead wants an OR-over-branches
/// match ("a push to ANY of these branches fires").
///
/// <para>Config schema (current shape):</para>
/// <code>
/// {
///   "repositoryId": "&lt;uuid&gt;",       // optional — absent ⇒ any repository
///   "branches": ["main", "release"]    // optional — absent/empty ⇒ any branch
/// }
/// </code>
///
/// <para>Match precedence:</para>
/// <list type="number">
///   <item>Empty config <c>{}</c> → true (no filter).</item>
///   <item><c>repositoryId</c> present + valid Guid → must equal the event's repository, else
///         false. A present-but-unparseable value is treated as "no repo filter" (mirrors the
///         PR filter's tolerance of malformed stored config).</item>
///   <item><c>branches</c> present + non-empty array → the event's branch (the <c>ref</c> with
///         a leading <c>refs/heads/</c> stripped) must equal one listed branch (OR, case-
///         sensitive per Git). Empty/absent/non-array → no branch filter.</item>
/// </list>
/// </summary>
internal static class PushTriggerMatcherFilter
{
    private const string BranchRefPrefix = "refs/heads/";

    public static bool Matches(JsonElement activationConfig, Guid eventRepositoryId, string eventRef)
    {
        if (activationConfig.ValueKind != JsonValueKind.Object) return true;

        return RepositoryMatches(activationConfig, eventRepositoryId) && BranchMatches(activationConfig, eventRef);
    }

    private static bool RepositoryMatches(JsonElement config, Guid eventRepositoryId)
    {
        if (!config.TryGetProperty("repositoryId", out var repoIdEl)) return true;
        if (repoIdEl.ValueKind != JsonValueKind.String) return true;
        if (!Guid.TryParse(repoIdEl.GetString(), out var configuredRepoId)) return true;

        return configuredRepoId == eventRepositoryId;
    }

    /// <summary>
    /// OR-filter: the event's branch must equal one of the configured branches. Empty / missing /
    /// non-array → no branch filter (any branch fires). Non-string entries are skipped so a
    /// half-typed config never blocks a match the operator clearly wanted.
    /// </summary>
    private static bool BranchMatches(JsonElement config, string eventRef)
    {
        if (!config.TryGetProperty("branches", out var branchesEl)) return true;
        if (branchesEl.ValueKind != JsonValueKind.Array) return true;
        if (branchesEl.GetArrayLength() == 0) return true;

        var eventBranch = ShortBranch(eventRef);

        foreach (var entry in branchesEl.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.String) continue;

            var name = entry.GetString();
            if (string.IsNullOrEmpty(name)) continue;

            if (string.Equals(ShortBranch(name), eventBranch, StringComparison.Ordinal)) return true;
        }

        return false;
    }

    /// <summary>Strip the <c>refs/heads/</c> prefix so a config of "main" matches a push ref of
    /// "refs/heads/main". A ref without the prefix (already short, or a tag/other ref) is returned
    /// unchanged — so a branch filter never matches a tag push.</summary>
    private static string ShortBranch(string gitRef) =>
        gitRef.StartsWith(BranchRefPrefix, StringComparison.Ordinal) ? gitRef[BranchRefPrefix.Length..] : gitRef;
}
