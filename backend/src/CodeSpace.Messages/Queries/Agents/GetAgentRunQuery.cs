using CodeSpace.Messages.Authorization;
using CodeSpace.Messages.Dtos.Agents;
using CodeSpace.Messages.Mediation;

namespace CodeSpace.Messages.Queries.Agents;

/// <summary>
/// The live status of one agent run for the caller's team — drives the run-detail's "Running · last
/// active Ns ago" header and the poll-while-active decision. Null when the run isn't this team's (a
/// foreign id leaks nothing).
/// </summary>
public sealed record GetAgentRunQuery : IQuery<AgentRunSummary?>, IRequireTeamMembership
{
    public required Guid AgentRunId { get; init; }
}
