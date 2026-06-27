using CodeSpace.Messages.Authorization;
using CodeSpace.Messages.Dtos.Workflows;
using CodeSpace.Messages.Mediation;

namespace CodeSpace.Messages.Queries.Workflows;

/// <summary>The per-cell attempt history for one <c>(NodeId, IterationKey)</c> cell of the lineage <see cref="RunId"/> belongs to.</summary>
public sealed record GetCellAttemptsQuery : IQuery<CellAttemptsResponse?>, IRequireTeamMembership
{
    public required Guid RunId { get; init; }
    public required string NodeId { get; init; }
    public required string IterationKey { get; init; }
}
