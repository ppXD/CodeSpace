using CodeSpace.Messages.Authorization;
using CodeSpace.Messages.Mediation;

namespace CodeSpace.Messages.Commands.Agents;

/// <summary>
/// Author a new agent directly INTO the Library (a store entry under the team's synthetic Custom pack), rather than
/// onto the runnable bench. Same editable surface as <see cref="CreateAgentDefinitionCommand"/>; the server sets
/// Origin=Authored, Scope=Store, and the Custom pack. You instantiate a working copy to run it. Returns the new id.
/// </summary>
public sealed record AuthorStoreAgentCommand : ICommand<Guid>, IRequireTeamMembership
{
    public required string Name { get; init; }
    public string? Description { get; init; }
    public string? SystemPrompt { get; init; }
    public string? Model { get; init; }
    public string? DefaultAutonomy { get; init; }
    public IReadOnlyList<string>? Tools { get; init; }
}
