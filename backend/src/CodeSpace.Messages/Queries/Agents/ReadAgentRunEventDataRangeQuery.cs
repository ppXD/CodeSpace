using CodeSpace.Messages.Authorization;
using CodeSpace.Messages.Dtos.Agents;
using CodeSpace.Messages.Mediation;

namespace CodeSpace.Messages.Queries.Agents;

/// <summary>Read one bounded range of an exact Agent Run event's offloaded, already-redacted harness-native payload.</summary>
public sealed record ReadAgentRunEventDataRangeQuery : IQuery<AgentRunEventDataRangeRead?>, IRequireTeamMembership
{
    public required Guid AgentRunId { get; init; }
    public required long EventSequence { get; init; }
    public long OffsetBytes { get; init; }
    public int LimitBytes { get; init; } = 64 * 1024;
}
