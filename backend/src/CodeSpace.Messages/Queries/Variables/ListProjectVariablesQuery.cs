using CodeSpace.Messages.Authorization;
using CodeSpace.Messages.Dtos.Variables;
using MediatR;

namespace CodeSpace.Messages.Queries.Variables;

/// <summary>
/// Lists every active project-scoped variable for a given project. Project ownership is
/// verified against the current team. Same shape as <see cref="ListWorkflowVariablesQuery"/>
/// + <see cref="ListTeamVariablesQuery"/> — the controller / detail UI binds against the
/// identical <see cref="VariableSummary"/> shape regardless of scope.
/// </summary>
public sealed record ListProjectVariablesQuery : IRequest<IReadOnlyList<VariableSummary>>, IRequireTeamMembership
{
    public Guid ProjectId { get; init; }
}
