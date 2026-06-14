using CodeSpace.Messages.Authorization;
using CodeSpace.Messages.Dtos.Agents;
using CodeSpace.Messages.Mediation;

namespace CodeSpace.Messages.Queries.Agents;

/// <summary>
/// The audit of every governed (side-effecting) MCP tool call an agent run made — what tool, when, the outcome,
/// and the approval trail. Team-scoped: a run that isn't the caller's team returns an empty list (leaks neither
/// tool calls nor existence). Read-only tools are NOT recorded (they skip the ledger), so they never appear here.
/// </summary>
public sealed record ListToolCallsQuery : IQuery<IReadOnlyList<ToolCallView>>, IRequireTeamMembership
{
    public required Guid AgentRunId { get; init; }
}
