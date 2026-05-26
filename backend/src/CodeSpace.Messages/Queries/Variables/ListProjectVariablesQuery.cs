using CodeSpace.Messages.Authorization;
using CodeSpace.Messages.Dtos.Variables;
using MediatR;

namespace CodeSpace.Messages.Queries.Variables;

public sealed record ListProjectVariablesQuery : IRequest<IReadOnlyList<VariableSummary>>, IRequireTeamMembership
{
    public required Guid ProjectId { get; init; }
}
