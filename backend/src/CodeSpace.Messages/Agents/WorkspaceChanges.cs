namespace CodeSpace.Messages.Agents;

/// <summary>
/// The agent's work product, captured from the workspace at run end: the unified diff of everything that
/// changed versus the cloned base, plus the changed-file paths. Ground truth read from git (staged +
/// unstaged + committed, vs the base revision) — not the agent's self-report. Empty when the agent
/// changed nothing.
/// </summary>
public sealed record WorkspaceChanges
{
    /// <summary>Unified diff (git format) of all changes vs the cloned base. Empty string when nothing changed.</summary>
    public required string Patch { get; init; }

    /// <summary>
    /// The cloned base revision the <see cref="Patch"/> is rooted at — the exact commit the agent saw, captured at
    /// clone (the SHA the diff is taken against). The integrity anchor for on-disk branch integration: the integrator
    /// checks out THIS SHA before applying the patch so a 3-way apply resolves against the right pre-image, and
    /// refuses a contribution whose base disagrees. Null only when no workspace recorded it.
    /// </summary>
    public string? BaseSha { get; init; }

    /// <summary>Repo-relative paths the agent changed (added / modified / deleted).</summary>
    public IReadOnlyList<string> ChangedFiles { get; init; } = Array.Empty<string>();

    /// <summary>Per-file line diffstat (added / removed counts) parsed from <c>git diff --numstat</c> — git ground truth, parallel to <see cref="ChangedFiles"/>. Empty when nothing changed; a binary file's counts are null. Captured so the "+X −Y" survives even when the full <see cref="Patch"/> is offloaded.</summary>
    public IReadOnlyList<FileDiffStat> FileStats { get; init; } = Array.Empty<FileDiffStat>();

    /// <summary>True when the agent left the workspace unchanged.</summary>
    public bool IsEmpty => string.IsNullOrEmpty(Patch) && ChangedFiles.Count == 0;
}
