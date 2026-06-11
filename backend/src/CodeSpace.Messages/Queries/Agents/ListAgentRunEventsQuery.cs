using CodeSpace.Messages.Authorization;
using CodeSpace.Messages.Dtos.Agents;
using CodeSpace.Messages.Mediation;

namespace CodeSpace.Messages.Queries.Agents;

/// <summary>
/// The agent run's live log, only the steps newer than <see cref="AfterSequence"/> (pass 0 for the whole
/// log) — the incremental cursor the run-detail timeline streams with. Team-scoped: a run that isn't the
/// caller's team returns an empty list (leaks neither events nor existence).
/// </summary>
public sealed record ListAgentRunEventsQuery : IQuery<IReadOnlyList<AgentRunEventDto>>, IRequireTeamMembership
{
    public required Guid AgentRunId { get; init; }
    public long AfterSequence { get; init; }
}
