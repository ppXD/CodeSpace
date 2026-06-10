using CodeSpace.Messages.Authorization;
using CodeSpace.Messages.Dtos.Agents;
using MediatR;

namespace CodeSpace.Messages.Queries.Agents;

/// <summary>Single-row read for the persona detail / edit page. Returns null on miss / not-yours.</summary>
public sealed record GetAgentDefinitionQuery : IRequest<AgentDefinitionSummary?>, IRequireTeamMembership
{
    public required Guid AgentDefinitionId { get; init; }
}
