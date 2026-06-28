using CodeSpace.Messages.Enums;

namespace CodeSpace.Messages.Dtos.Agents;

/// <summary>
/// Level-2 view of a single Skill (the DETAIL read) — the <see cref="SkillDefinitionSummary"/> fields PLUS the
/// SKILL.md instruction <see cref="Body"/> and the verbatim <see cref="RawFrontmatterJson"/>. Returned for one
/// skill at a time so the body's token cost is only paid when the skill is actually opened.
/// </summary>
public sealed record SkillDefinitionDetail
{
    public required Guid Id { get; init; }
    public required Guid TeamId { get; init; }
    public required string Slug { get; init; }
    public required string Name { get; init; }
    public string? Description { get; init; }
    public required string Body { get; init; }
    public string? Category { get; init; }
    public required string RawFrontmatterJson { get; init; }
    public required SkillDefinitionOrigin Origin { get; init; }
    public Guid? PackId { get; init; }
    public Guid? SourceDefinitionId { get; init; }
    public string? SourcePath { get; init; }
    public required DateTimeOffset CreatedDate { get; init; }
}
