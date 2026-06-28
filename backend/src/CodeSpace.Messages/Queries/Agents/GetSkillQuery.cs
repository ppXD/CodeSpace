using CodeSpace.Messages.Authorization;
using CodeSpace.Messages.Dtos.Agents;
using MediatR;

namespace CodeSpace.Messages.Queries.Agents;

/// <summary>Single-skill detail read (Level-2: includes the SKILL.md body) for the Library detail modal. Null on miss / not-yours.</summary>
public sealed record GetSkillQuery : IRequest<SkillDefinitionDetail?>, IRequireTeamMembership
{
    public required Guid SkillDefinitionId { get; init; }
}
