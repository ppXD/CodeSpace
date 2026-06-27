using CodeSpace.Messages.Enums;

namespace CodeSpace.Messages.Dtos.Agents;

/// <summary>
/// Level-1 row for the skill library / store (the LIST surface). Deliberately omits the SKILL.md
/// <c>Body</c> — the whole point of skills is progressive disclosure, so a library of hundreds stays cheap to
/// list (name + description + grouping only); the body is fetched per-skill via the detail read. <see cref="PackId"/>
/// + <see cref="Origin"/> let the UI show which library a skill came from (a store concept) vs an authored one.
/// </summary>
public sealed record SkillDefinitionSummary
{
    public required Guid Id { get; init; }
    public required Guid TeamId { get; init; }
    public required string Slug { get; init; }
    public required string Name { get; init; }
    public string? Description { get; init; }
    public string? Category { get; init; }
    public required SkillDefinitionOrigin Origin { get; init; }
    public Guid? PackId { get; init; }
    public required DateTimeOffset CreatedDate { get; init; }
}
