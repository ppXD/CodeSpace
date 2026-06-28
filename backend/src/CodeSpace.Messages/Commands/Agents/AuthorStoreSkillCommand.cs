using CodeSpace.Messages.Authorization;
using CodeSpace.Messages.Mediation;

namespace CodeSpace.Messages.Commands.Agents;

/// <summary>
/// Author a new skill directly INTO the Library (a store entry under the team's synthetic Custom pack). The server
/// sets Origin=Authored, Scope=Store, and the Custom pack. It's not bindable as-is — you instantiate a working copy
/// (via <c>InstantiateSkillFromStoreCommand</c>) to bind it to an agent. Returns the new id.
/// </summary>
public sealed record AuthorStoreSkillCommand : ICommand<Guid>, IRequireTeamMembership
{
    public required string Name { get; init; }
    public string? Description { get; init; }
    public string? Body { get; init; }
    public string? Category { get; init; }
}
