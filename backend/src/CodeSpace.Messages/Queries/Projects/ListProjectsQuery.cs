using CodeSpace.Messages.Authorization;
using CodeSpace.Messages.Dtos.Projects;
using MediatR;

namespace CodeSpace.Messages.Queries.Projects;

/// <summary>
/// All active projects for the caller's team, ordered by created_date ASC so the auto-
/// seeded "default" project comes first. The list page renders the result as cards with
/// repository + variable counts.
/// </summary>
public sealed record ListProjectsQuery : IRequest<IReadOnlyList<ProjectSummary>>, IRequireTeamMembership
{
}
