using CodeSpace.Messages.Authorization;
using CodeSpace.Messages.Dtos.Agents;
using CodeSpace.Messages.Mediation;

namespace CodeSpace.Messages.Queries.Agents;

public sealed record ListAgentRunLogsQuery : IQuery<AgentRunLogPage?>, IRequireTeamMembership
{
    public required Guid AgentRunId { get; init; }
    public string? Cursor { get; init; }
    public int Limit { get; init; } = 50;
}

public sealed record GetAgentRunLogQuery : IQuery<AgentRunLogStreamSummary?>, IRequireTeamMembership
{
    public required Guid AgentRunId { get; init; }
    public required Guid StreamId { get; init; }
}

public sealed record ReadAgentRunLogRangeQuery : IQuery<AgentRunLogRangeRead?>, IRequireTeamMembership
{
    public required Guid AgentRunId { get; init; }
    public required Guid StreamId { get; init; }
    public long OffsetBytes { get; init; }
    public int LimitBytes { get; init; } = 64 * 1024;
}
