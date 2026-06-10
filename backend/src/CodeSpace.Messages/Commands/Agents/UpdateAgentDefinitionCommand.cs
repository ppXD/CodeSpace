using CodeSpace.Messages.Authorization;
using CodeSpace.Messages.Mediation;
using MediatR;

namespace CodeSpace.Messages.Commands.Agents;

/// <summary>
/// Replace the editable surface of an existing persona (full-replace / PUT semantics: every authored
/// field is set from the command). The <see cref="AgentDefinitionOrigin"/>, slug, and the import-owned
/// fields (skills / MCP / verbatim frontmatter / provenance) are intentionally NOT touched — editing an
/// imported persona's prompt or model leaves its imported skills + re-sync provenance intact. The slug
/// is immutable post-create (it's the @-mention handle other definitions / chats reference).
/// </summary>
public sealed record UpdateAgentDefinitionCommand : ICommand<Unit>, IRequireTeamMembership
{
    public required Guid AgentDefinitionId { get; init; }
    public required string Name { get; init; }
    public string? Description { get; init; }
    public string? SystemPrompt { get; init; }
    public string? Model { get; init; }
    public string? DefaultAutonomy { get; init; }
    public IReadOnlyList<string>? Tools { get; init; }
}
