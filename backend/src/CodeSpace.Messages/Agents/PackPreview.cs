namespace CodeSpace.Messages.Agents;

/// <summary>
/// A DRY-RUN view of a pack discovered at a URL — agents AND skills, the unified result of clone → recursive
/// discovery → per-team conflict check. Nothing is persisted. The operator inspects each item's full structure,
/// sees which are importable vs slug-conflicting, selects, and only then commits. The URL-driven, multi-format
/// successor to the agent-only <see cref="AgentPackPreview"/>.
/// </summary>
public sealed record PackPreview
{
    /// <summary>The git ref the pack was read at (the branch/tag requested), echoed for the import call. Null → the source's default branch.</summary>
    public string? Reference { get; init; }

    public IReadOnlyList<AgentPackPreviewItem> Agents { get; init; } = Array.Empty<AgentPackPreviewItem>();

    public IReadOnlyList<SkillPackPreviewItem> Skills { get; init; } = Array.Empty<SkillPackPreviewItem>();
}
