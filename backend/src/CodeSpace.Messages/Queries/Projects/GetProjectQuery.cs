using CodeSpace.Messages.Authorization;
using CodeSpace.Messages.Dtos.Projects;
using MediatR;

namespace CodeSpace.Messages.Queries.Projects;

/// <summary>Single-project read by id within the current team. Returns null when not found.</summary>
public sealed record GetProjectQuery : IRequest<ProjectSummary?>, IRequireTeamMembership
{
    public required Guid ProjectId { get; init; }
}
