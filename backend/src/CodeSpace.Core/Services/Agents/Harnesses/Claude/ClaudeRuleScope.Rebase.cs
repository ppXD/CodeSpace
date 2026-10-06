using System.Text.RegularExpressions;

namespace CodeSpace.Core.Services.Agents.Harnesses.Claude;

/// <summary>
/// A scoped rule's globs as a pointer rule in the run's config home carries them (<see cref="ClaudeWorkspaceMemory"/>).
/// The CLI matches a project rule's globs against the path of the file read, relative to the directory that holds the
/// rule's <c>.claude</c>, and a user rule's against the path relative to the run's cwd — both with the <c>ignore</c>
/// library's gitignore semantics. So a glob is rebased onto the cwd the way gitignore anchors it: as written when the
/// rule's directory is the cwd; below that directory when a slash anywhere but at its end anchors it there; at any depth
/// below it when nothing does. A leading <c>!</c> keeps negating. Observed against 2.1.263, where the unpinned CLI
/// attaching the rule and the pinned one attaching its pointer agree read for read (RepositoryConfigE2ETests).
///
/// <para>Only what the runner can re-emit exactly is rebased: letters, digits and <c>. _ + - / * ?</c>, with one
/// leading <c>!</c>, no empty, <c>.</c> or <c>..</c> segment. Nothing else is needed to write a glob the CLI reads
/// back as it is — no quote, backslash, space, brace, comma, colon, <c>#</c>, <c>[</c> — and no <c>@</c>, which a
/// pointer never holds anywhere. A rule with any other glob gets no pointer at all: dropping only that glob could drop a
/// negation and widen what the rule covers.</para>
/// </summary>
internal static partial class ClaudeRuleScope
{
    /// <summary>What a glob a pointer carries may hold: one leading <c>!</c>, then letters, digits and <c>. _ + - / * ?</c>.</summary>
    [GeneratedRegex(@"^!?[A-Za-z0-9._+\-/*?]+\z")]
    private static partial Regex SafeGlob();

    /// <summary>What the directory a glob is rebased below may be, relative to the cwd: segments of letters, digits and <c>. _ + -</c>.</summary>
    [GeneratedRegex(@"^[A-Za-z0-9._+\-]+(/[A-Za-z0-9._+\-]+)*\z")]
    private static partial Regex SafeDirectory();

    /// <summary>
    /// <paramref name="glob"/>, read from a rule whose <c>.claude</c> sits in the directory <paramref name="below"/> the
    /// cwd (empty for the cwd itself), as a glob that matches the same files relative to the cwd; null when either holds
    /// what a pointer cannot carry exactly.
    /// </summary>
    public static string? Rebase(string below, string glob)
    {
        if (!IsSafe(glob) || (below.Length > 0 && !IsSafeDirectory(below))) return null;

        if (below.Length == 0) return glob;

        var negation = glob.StartsWith('!') ? "!" : "";
        var pattern = glob[negation.Length..];

        return negation + (IsAnchored(pattern) ? $"/{below}/{pattern.TrimStart('/')}" : $"/{below}/**/{pattern}");
    }

    /// <summary>
    /// A pointer rule's frontmatter for <paramref name="globs"/>: each one a double-quoted list item. A glob that ends in
    /// <c>/**</c> is written with one more, because the CLI drops one from what it reads (<see cref="Normalise"/>), so
    /// <see cref="Read"/> of the frontmatter gives back exactly <paramref name="globs"/>.
    /// </summary>
    public static string Frontmatter(IEnumerable<string> globs) =>
        "---\npaths:\n" + string.Concat(globs.Select(glob => $"  - \"{(glob.EndsWith("/**", StringComparison.Ordinal) ? glob + "/**" : glob)}\"\n")) + "---\n";

    /// <summary>Whether the glob holds only what a pointer re-emits exactly, and no empty, <c>.</c> or <c>..</c> segment; one leading and one trailing slash are gitignore's own anchor and directory marks.</summary>
    private static bool IsSafe(string glob)
    {
        if (!SafeGlob().IsMatch(glob)) return false;

        var pattern = glob.TrimStart('!');
        var inner = pattern[(pattern.StartsWith('/') ? 1 : 0)..];

        return (inner.EndsWith('/') ? inner[..^1] : inner).Split('/').All(segment => segment.Length > 0 && segment != "." && segment != "..");
    }

    private static bool IsSafeDirectory(string below) => SafeDirectory().IsMatch(below) && below.Split('/').All(segment => segment != "." && segment != "..");

    /// <summary>gitignore's rule, as the <c>ignore</c> library applies it: a slash anywhere but at the end ties the pattern to its directory; otherwise it matches at any depth.</summary>
    private static bool IsAnchored(string pattern) => pattern.TrimEnd('/').Contains('/');
}
