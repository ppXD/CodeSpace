using CodeSpace.Messages.Enums;

namespace CodeSpace.Messages.Dtos.Agents;

/// <summary>
/// Operator-facing row for the Agents library (list + detail). The @-mention <see cref="Slug"/> is the
/// stable handle an <c>agent.code</c> node / a chat mention resolves against. <see cref="Tools"/> is
/// null when the persona inherits the harness's default toolset (distinct from an empty list = no tools).
/// <see cref="Origin"/> distinguishes an authored persona from an imported one (the latter is re-syncable
/// from its pack). Skills / MCP / verbatim frontmatter are owned by the import slice and not surfaced here.
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
    public required DateTimeOffset CreatedDate { get; init; }
}
