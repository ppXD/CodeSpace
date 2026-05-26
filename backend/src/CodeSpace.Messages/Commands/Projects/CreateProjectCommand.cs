using CodeSpace.Messages.Authorization;
using MediatR;

namespace CodeSpace.Messages.Commands.Projects;

/// <summary>
/// Create a new Project under the current team. Slug must be unique within the team and
/// match the slug regex (alphanumeric + underscore + hyphen, 1-64 chars). Team comes from
/// <c>X-Team-Id</c> header — not in the body.
/// </summary>
public sealed record CreateProjectCommand : IRequest<Guid>, IRequireTeamMembership
{
    public required string Slug { get; init; }
    public required string Name { get; init; }
    public string? Description { get; init; }
}
