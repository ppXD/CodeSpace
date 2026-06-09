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

    /// <summary>Repo-relative paths the agent changed (added / modified / deleted).</summary>
    public IReadOnlyList<string> ChangedFiles { get; init; } = Array.Empty<string>();

    /// <summary>True when the agent left the workspace unchanged.</summary>
    public bool IsEmpty => string.IsNullOrEmpty(Patch) && ChangedFiles.Count == 0;
}
