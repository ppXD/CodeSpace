namespace CodeSpace.Messages.Agents;

/// <summary>
/// The authorable surface of a Skill, consolidated into one record (same convention as
/// <see cref="AgentDefinitionInput"/>). AUTHORING contract only: the import slice owns <c>RawFrontmatter</c> /
/// provenance (pack + source path), so those are intentionally absent here and left untouched by create/update.
/// </summary>
public sealed record SkillDefinitionInput
{
    /// <summary>Human-readable name; the handle slug is derived from it (operators never type the handle).</summary>
    public required string Name { get; init; }

    /// <summary>The trigger/router text ("Use when…") — when the skill applies. Optional but strongly encouraged.</summary>
    public string? Description { get; init; }

    /// <summary>The SKILL.md instruction body (Level 2). Null/omitted = an empty body.</summary>
    public string? Body { get; init; }

    /// <summary>Grouping label for the library UI. Null = ungrouped.</summary>
    public string? Category { get; init; }
}
