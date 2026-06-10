namespace CodeSpace.Messages.Agents;

/// <summary>
/// A DRY-RUN view of an agent pack discovered at a source — nothing is persisted. The operator inspects
/// each discovered agent's full structure here, selects which to take, and only then commits via import.
/// </summary>
public sealed record AgentPackPreview
{
    /// <summary>The git ref the pack was read at (the resolved branch/commit), echoed for the import call.</summary>
    public string? Reference { get; init; }

    /// <summary>The directory the agents were discovered under (e.g. "agents").</summary>
    public required string RootPath { get; init; }

    public IReadOnlyList<AgentPackPreviewItem> Items { get; init; } = Array.Empty<AgentPackPreviewItem>();
}

/// <summary>
/// One discovered agent in a pack preview — the FULL parsed structure the operator inspects before
/// committing (prompt / model / tools / verbatim frontmatter), plus the derived @-handle, a
/// slug-conflict flag (an active persona with that handle already exists in the team), and whether it's
/// importable at all (parseable + named). Keyed by <see cref="SourcePath"/> — the stable per-file identity
/// the import call selects on.
/// </summary>
public sealed record AgentPackPreviewItem
{
    public required string SourcePath { get; init; }
    public required string Name { get; init; }
    public required string DerivedSlug { get; init; }
    public string? Description { get; init; }
    public string SystemPrompt { get; init; } = "";
    public string? Model { get; init; }
    public IReadOnlyList<string>? Tools { get; init; }
    public string RawFrontmatterJson { get; init; } = "{}";

    /// <summary>Parse warnings/errors for this file (missing name, invalid YAML, fetch failure).</summary>
    public IReadOnlyList<string> Diagnostics { get; init; } = Array.Empty<string>();

    /// <summary>True when an active persona with the derived handle already exists in the team — import will SKIP this one.</summary>
    public bool SlugConflict { get; init; }

    /// <summary>True when this agent can be imported (parseable + has a name + no slug conflict). The UI defaults its selection to importable items.</summary>
    public bool Importable { get; init; }
}
