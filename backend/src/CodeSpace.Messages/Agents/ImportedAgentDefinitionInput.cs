namespace CodeSpace.Messages.Agents;

/// <summary>
/// The write contract for IMPORTING a persona — deliberately separate from <c>AgentDefinitionInput</c>
/// (which is authored-only and must stay that way; three call sites depend on that boundary). Carries the
/// authorable surface PLUS the import-owned fields a parsed artifact produces: the verbatim frontmatter,
/// MCP references, and provenance (SourcePath, optional PackId). Skills are bound relationally via
/// <c>AgentSkillBinding</c>, not carried here. The service stamps <c>Origin = Imported</c> — it's never on
/// this input, so a public authoring path can't forge it.
/// </summary>
public sealed record ImportedAgentDefinitionInput
{
    public required string Name { get; init; }
    public string? Description { get; init; }
    public string? SystemPrompt { get; init; }
    public string? Model { get; init; }
    public string? DefaultAutonomy { get; init; }

    /// <summary>Tool allow-list — null = harness default, empty = no tools, non-empty = exactly these (same tri-state as the persona entity).</summary>
    public IReadOnlyList<string>? Tools { get; init; }

    /// <summary>MCP server references as JSON (default "[]"); jsonb on the entity.</summary>
    public string McpServersJson { get; init; } = "[]";

    /// <summary>The imported artifact's frontmatter, verbatim (the lossless forward-compat source).</summary>
    public required string RawFrontmatterJson { get; init; }

    /// <summary>Path within the source the artifact came from (e.g. "agents/backend-architect.md") — re-sync provenance.</summary>
    public required string SourcePath { get; init; }

    /// <summary>The agent pack this was imported from, when one exists. Null in v1 (no pack table yet) — provenance rides on Origin + SourcePath.</summary>
    public Guid? PackId { get; init; }
}
