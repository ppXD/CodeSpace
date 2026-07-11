using CodeSpace.Messages.Enums;

namespace CodeSpace.Messages.Dtos.Agents;

/// <summary>
/// Operator-facing row for the Agents library (list + detail). The @-mention <see cref="Slug"/> is the
/// stable handle an <c>agent.run</c> node / a chat mention resolves against. <see cref="Tools"/> is
/// null when the persona inherits the harness's default toolset (distinct from an empty list = no tools).
/// <see cref="Origin"/> distinguishes an authored persona from an imported one (the latter is re-syncable
/// from its pack). <see cref="BoundSkills"/> are the skills the persona carries, read from the
/// <c>AgentSkillBinding</c> join (the relational replacement for the dropped <c>skills_jsonb</c> blob).
/// MCP / verbatim frontmatter remain import-owned and are not surfaced here.
/// </summary>
public sealed record AgentDefinitionSummary
{
    public required Guid Id { get; init; }
    public required Guid TeamId { get; init; }
    public required string Slug { get; init; }
    public required string Name { get; init; }
    public string? Description { get; init; }
    public required string SystemPrompt { get; init; }
    public string? Model { get; init; }
    public string? DefaultAutonomy { get; init; }
    public IReadOnlyList<string>? Tools { get; init; }
    public required AgentDefinitionOrigin Origin { get; init; }

    /// <summary>The source pack's name (its `owner/repo`) for an imported persona, so the bench can show where it came from. NULL for an authored persona (or an imported one whose pack was removed).</summary>
    public string? PackName { get; init; }

    /// <summary>The skills bound to this persona (handle + name), ordered by handle. Empty when none are bound.</summary>
    public IReadOnlyList<AgentBoundSkill> BoundSkills { get; init; } = Array.Empty<AgentBoundSkill>();

    public required DateTimeOffset CreatedDate { get; init; }
}
