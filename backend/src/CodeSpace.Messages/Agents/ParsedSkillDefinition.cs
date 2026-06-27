namespace CodeSpace.Messages.Agents;

/// <summary>
/// The harness-neutral result of parsing ONE skill artifact (a <c>SKILL.md</c>) — the thin Level-1 index
/// (name/description/category) projected into first-class fields, the Level-2 instruction <see cref="Body"/>,
/// PLUS the original frontmatter preserved VERBATIM in <see cref="RawFrontmatterJson"/> (the lossless
/// forward-compat contract: unknown / nested / future keys ride along untouched). Pure data: the import
/// preview/commit layer interprets it; the parser never touches the DB. A malformed or incomplete artifact
/// still parses to a value with <see cref="Diagnostics"/> populated, so a bad file surfaces in the preview
/// rather than aborting the whole pack. Mirrors <see cref="ParsedAgentDefinition"/>.
/// </summary>
public sealed record ParsedSkillDefinition
{
    /// <summary>Path of the artifact within its source (e.g. "skills/test-driven-development/SKILL.md") — the stable per-file identity used to select for import + as the sync key.</summary>
    public required string SourcePath { get; init; }

    /// <summary>The skill name (frontmatter <c>name</c>). Empty when the artifact has no usable name — see <see cref="Diagnostics"/>; such an item is shown un-importable in the preview.</summary>
    public string Name { get; init; } = "";

    /// <summary>The trigger/router text (frontmatter <c>description</c>) — when the skill applies. Null when absent (flagged in <see cref="Diagnostics"/>).</summary>
    public string? Description { get; init; }

    /// <summary>The SKILL.md instruction body — the markdown after the frontmatter block (Level 2).</summary>
    public string Body { get; init; } = "";

    /// <summary>Grouping label (frontmatter <c>category</c>) if present; null lets the import layer derive one (pack folder / "Uncategorized").</summary>
    public string? Category { get; init; }

    /// <summary>The ENTIRE parsed frontmatter, re-serialized to JSON verbatim — the lossless source unknown/future keys survive in.</summary>
    public string RawFrontmatterJson { get; init; } = "{}";

    /// <summary>Per-file parse warnings/errors (missing name/description, invalid YAML, …). Missing name = previewable but not importable.</summary>
    public IReadOnlyList<string> Diagnostics { get; init; } = Array.Empty<string>();
}
