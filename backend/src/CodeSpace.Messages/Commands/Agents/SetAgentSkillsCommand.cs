using CodeSpace.Messages.Authorization;
using CodeSpace.Messages.Mediation;
using MediatR;

namespace CodeSpace.Messages.Commands.Agents;

/// <summary>
/// Replace the set of skills bound to a persona (full-replace semantics: the binding set becomes exactly
/// <see cref="SkillDefinitionIds"/> — absent ids are unbound, new ids are bound). This is the in-app
/// counterpart to the import path's <c>skills:</c> declaration; both write the same AgentSkillBinding join.
/// The agent and every skill id are validated to belong to the caller's team before any write.
/// </summary>
public sealed record SetAgentSkillsCommand : ICommand<Unit>, IRequireTeamMembership
{
    public required Guid AgentDefinitionId { get; init; }
    public IReadOnlyList<Guid> SkillDefinitionIds { get; init; } = Array.Empty<Guid>();
}
