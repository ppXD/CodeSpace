using CodeSpace.Messages.Authorization;
using CodeSpace.Messages.Dtos.Agents;
using MediatR;

namespace CodeSpace.Messages.Queries.Agents;

/// <summary>
/// All active Agent personas for the caller's team, ordered by created_date ASC. Drives the Agents
/// library list and the @-mention / editor-palette pickers.
/// </summary>
public sealed record ListAgentDefinitionsQuery : IRequest<IReadOnlyList<AgentDefinitionSummary>>, IRequireTeamMembership
{
}
