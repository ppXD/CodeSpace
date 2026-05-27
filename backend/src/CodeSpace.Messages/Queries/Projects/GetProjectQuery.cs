using CodeSpace.Messages.Authorization;
using CodeSpace.Messages.Dtos.Projects;
using MediatR;

namespace CodeSpace.Messages.Queries.Projects;

/// <summary>Single-row read for the project detail page. Returns null on miss / not-yours.</summary>
public sealed record GetProjectQuery : IRequest<ProjectSummary?>, IRequireTeamMembership
{
    public required Guid ProjectId { get; init; }
}
