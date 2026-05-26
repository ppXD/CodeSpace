using CodeSpace.Messages.Authorization;
using CodeSpace.Messages.Dtos.Projects;
using MediatR;

namespace CodeSpace.Messages.Queries.Projects;

/// <summary>List active Projects in the current team. Team comes from <c>X-Team-Id</c> header.</summary>
public sealed record ListProjectsQuery : IRequest<IReadOnlyList<ProjectSummary>>, IRequireTeamMembership;
