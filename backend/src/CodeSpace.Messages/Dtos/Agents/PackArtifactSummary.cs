using CodeSpace.Messages.Enums;

namespace CodeSpace.Messages.Dtos.Agents;

/// <summary>
/// One artifact inside a pack — an agent persona or a skill — as the Library/store detail lists them
/// uniformly (icon + name + @handle + description). The Level-1 fields only; the full agent/skill is fetched
/// from its own detail read.
/// </summary>
public sealed record PackArtifactSummary
{
    public required PackArtifactKind Kind { get; init; }
    public required Guid Id { get; init; }
    public required string Slug { get; init; }
    public required string Name { get; init; }
    public string? Description { get; init; }
    public string? SourcePath { get; init; }
}
