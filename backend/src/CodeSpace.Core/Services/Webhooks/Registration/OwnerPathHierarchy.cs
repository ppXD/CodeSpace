namespace CodeSpace.Core.Services.Webhooks.Registration;

/// <summary>
/// Owner paths, read as the tree they are. A GitLab group full path nests — <c>acme</c> holds
/// <c>acme/platform</c> holds <c>acme/platform/web</c> — and a hook registered on a group covers
/// every project in it AND in its subgroups (GitLab's own words:  group webhooks "send notifications
/// for events across all projects in a group and its subgroups"). A GitHub organization login has no
/// separator, so every path here is its own only ancestor and the nesting question never arises,
/// which is what makes one rule serve both.
///
/// <para>Pure and static because this is the whole of the decision "does an existing hook already
/// cover this owner" — a decision worth testing without a database.</para>
/// </summary>
public static class OwnerPathHierarchy
{
    /// <summary>
    /// <paramref name="ownerPath"/> and every path above it, nearest first: <c>acme/platform/web</c>
    /// yields itself, then <c>acme/platform</c>, then <c>acme</c>. Nearest first so a caller that
    /// wants "the closest hook covering this" gets it by taking the first match.
    /// </summary>
    public static IReadOnlyList<string> SelfAndAncestors(string ownerPath)
    {
        var segments = ownerPath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var paths = new List<string>(segments.Length);

        for (var depth = segments.Length; depth > 0; depth--)
            paths.Add(string.Join('/', segments.Take(depth)));

        return paths;
    }

    /// <summary>
    /// True when a hook on <paramref name="ancestorPath"/> covers <paramref name="ownerPath"/>.
    /// Segment-aware rather than a string prefix: <c>acme/plat</c> is a prefix of
    /// <c>acme/platform</c> and covers nothing in it.
    /// </summary>
    public static bool Covers(string ancestorPath, string ownerPath) =>
        SelfAndAncestors(ownerPath).Contains(ancestorPath, StringComparer.Ordinal);
}
