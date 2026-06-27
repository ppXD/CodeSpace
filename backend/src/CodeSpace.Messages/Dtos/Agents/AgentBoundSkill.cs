namespace CodeSpace.Messages.Dtos.Agents;

/// <summary>
/// A skill bound to an agent persona — the Level-1 handle the UI renders as a chip on the agent row/editor.
/// The relational replacement for the dropped <c>skills_jsonb</c> blob: read from the <c>AgentSkillBinding</c>
/// join through to the active <c>SkillDefinition</c>.
/// </summary>
public sealed record AgentBoundSkill
{
    public required Guid SkillDefinitionId { get; init; }
    public required string Slug { get; init; }
    public required string Name { get; init; }
}
