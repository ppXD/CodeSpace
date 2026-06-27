namespace CodeSpace.Messages.Agents;

/// <summary>
/// One discovered skill in a pack preview — the FULL parsed structure the operator inspects before committing
/// (name / description / body / verbatim frontmatter), plus the derived handle, a slug-conflict flag (an active
/// skill with that handle already exists in the team), and whether it's importable at all (parseable + named +
/// no conflict). The skill peer of <see cref="AgentPackPreviewItem"/>; keyed by <see cref="SourcePath"/>.
/// </summary>
public sealed record SkillPackPreviewItem
{
    public required string SourcePath { get; init; }
    public required string Name { get; init; }
    public required string DerivedSlug { get; init; }
    public string? Description { get; init; }
    public string Body { get; init; } = "";
    public string? Category { get; init; }
    public string RawFrontmatterJson { get; init; } = "{}";

    /// <summary>Parse warnings/errors for this file (missing name/description, non-strict-YAML fallback).</summary>
    public IReadOnlyList<string> Diagnostics { get; init; } = Array.Empty<string>();

    /// <summary>True when an active skill with the derived handle already exists in the team — import will SKIP this one.</summary>
    public bool SlugConflict { get; init; }

    /// <summary>True when this skill can be imported (parseable + has a name + no slug conflict). The UI defaults its selection to importable items.</summary>
    public bool Importable { get; init; }
}
