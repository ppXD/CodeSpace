using CodeSpace.Messages.Authorization;
using CodeSpace.Messages.Dtos.Workflows;
using CodeSpace.Messages.Mediation;

namespace CodeSpace.Messages.Queries.Workflows;

/// <summary>The attempt ladder of the lineage <see cref="RunId"/> belongs to (any member resolves the same ladder).</summary>
public sealed record GetRunAttemptsQuery : IQuery<RunAttemptsResponse?>, IRequireTeamMembership
{
    public required Guid RunId { get; init; }
}
