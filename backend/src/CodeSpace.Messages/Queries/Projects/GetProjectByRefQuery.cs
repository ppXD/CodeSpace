using CodeSpace.Messages.Authorization;
using CodeSpace.Messages.Dtos.Projects;
using MediatR;

namespace CodeSpace.Messages.Queries.Projects;

/// <summary>
/// Single-row read for the project detail page, keyed by a URL reference that is EITHER a
/// GUID (legacy link) or the team-unique slug (canonical clean URL). Returns null on
/// miss / not-yours. The router uses the returned <c>Slug</c> to canonicalise a legacy-GUID
/// URL to the slug URL.
/// </summary>
public sealed record GetProjectByRefQuery : IRequest<ProjectSummary?>, IRequireTeamMembership
{
    public required string IdOrSlug { get; init; }
}
