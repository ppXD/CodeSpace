using CodeSpace.Messages.Agents;

namespace CodeSpace.Core.Services.Agents.Workspace;

/// <summary>
/// PURE parse of <c>git diff --numstat</c> output into per-file <see cref="FileDiffStat"/> rows. Each numstat line is
/// <c>&lt;added&gt;\t&lt;deleted&gt;\t&lt;path&gt;</c>; a BINARY file reports <c>-\t-\t&lt;path&gt;</c> (counts → null,
/// no line concept). Robust to blank lines and malformed rows (skipped, never thrown) so a git quirk never fails a
/// capture. Extracted so the parse is unit-pinned without invoking git. Internal — the workspace provider's helper.
/// </summary>
internal static class NumstatParser
{
    public static IReadOnlyList<FileDiffStat> Parse(string? numstat)
    {
        if (string.IsNullOrWhiteSpace(numstat)) return Array.Empty<FileDiffStat>();

        var stats = new List<FileDiffStat>();

        foreach (var line in numstat.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = line.Split('\t');

            if (parts.Length < 3) continue;   // a well-formed numstat row is added\tdeleted\tpath — skip anything else

            // The path is the third field; a path git had to quote (a real tab in the name) split into extra fields —
            // rejoin them. A rename ("old => new") lives in ONE field (spaces, not tabs), so it rejoins verbatim, then
            // resolves to its NEW name below to match the changed-file list.
            var path = RenameNewPath(string.Join('\t', parts[2..]).Trim());

            if (path.Length == 0) continue;

            stats.Add(new FileDiffStat(path, ParseCount(parts[0]), ParseCount(parts[1])));
        }

        return stats;
    }

    /// <summary>A numstat count column: a non-negative integer, or "-" for a binary file (→ null, no line count).</summary>
    private static int? ParseCount(string token) => token != "-" && int.TryParse(token, out var n) ? n : null;

    private const string Arrow = " => ";

    /// <summary>
    /// Resolve a numstat RENAME path to its POST-rename name — git renders a rename as <c>pre/{old => new}/post</c>
    /// (the common prefix/suffix folded out) or a bare <c>old => new</c>, but lists the file by its NEW name in
    /// <c>--name-only</c> (which drives the changed-file list). Resolving to that new name lets the per-file stat JOIN
    /// the changed-file list, with git's accurate net counts (rename detection ON — a moved+edited file counts only its
    /// real line delta, not a full re-add). A non-rename path (no <c>=&gt;</c>) passes through unchanged.
    /// </summary>
    private static string RenameNewPath(string path)
    {
        if (!path.Contains(Arrow, StringComparison.Ordinal)) return path;

        var open = path.IndexOf('{');
        var close = path.IndexOf('}');

        if (open >= 0 && close > open)
        {
            var inside = path.Substring(open + 1, close - open - 1);                       // "old => new"
            var newInside = inside[(inside.IndexOf(Arrow, StringComparison.Ordinal) + Arrow.Length)..];
            return (path[..open] + newInside + path[(close + 1)..]).Replace("//", "/");     // fold the empty-side "dir/{old => }/f" → "dir/f"
        }

        return path[(path.IndexOf(Arrow, StringComparison.Ordinal) + Arrow.Length)..].Trim();
    }
}
