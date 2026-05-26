using System.Text.RegularExpressions;

namespace CodeSpace.Core.Services.Providers.Markdown;

/// <summary>
/// Counts GitHub-flavored-markdown task-list items (<c>- [ ]</c> / <c>- [x]</c>) in a
/// PR or MR description body. Both GitHub and GitLab render these the same way, so the
/// logic is provider-neutral.
/// </summary>
/// <remarks>
/// Matches a leading dash / asterisk / plus, optional whitespace, then <c>[ ]</c> or
/// <c>[x]</c> (case-insensitive) at the start of a line. Doesn't try to parse list
/// nesting — a deeply indented task still counts, matching what GitHub does. We
/// deliberately don't full-parse markdown here; for counts this regex is faster and
/// produces the same numbers GitHub shows next to the "X of Y tasks" pill.
/// </remarks>
public static class TaskListCounter
{
    // ^ start of line, then optional indent + bullet + [ ] or [x|X].
    // RegexOptions.Multiline so `^` matches at every line boundary.
    private static readonly Regex TaskItemRegex = new(
        @"^\s*[-*+]\s+\[([ xX])\]",
        RegexOptions.Multiline | RegexOptions.Compiled);

    /// <summary>
    /// Returns <c>(completed, total)</c>. Both null when the body has no task items —
    /// callers surface "no task list at all" as a hidden badge, not "0 of 0".
    /// </summary>
    public static (int? completed, int? total) Count(string? body)
    {
        if (string.IsNullOrEmpty(body)) return (null, null);

        var matches = TaskItemRegex.Matches(body);
        if (matches.Count == 0) return (null, null);

        var completed = 0;
        foreach (Match m in matches)
        {
            // Group 1 captures the character inside the brackets — space = open,
            // x/X = done.
            if (m.Groups[1].Value is "x" or "X") completed++;
        }
        return (completed, matches.Count);
    }
}
