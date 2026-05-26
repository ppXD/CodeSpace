using CodeSpace.Messages.Authorization;
using MediatR;

namespace CodeSpace.Messages.Commands.Projects;

/// <summary>
/// Rename / re-describe an existing project. Slug is intentionally NOT editable post-create
/// — changing it would invalidate every existing <c>{{project.{Slug}.X}}</c> reference in
/// every workflow that uses this project. If operators need a different slug, they create
/// a new project + re-attach repositories.
/// </summary>
public sealed record UpdateProjectCommand : IRequest<MediatR.Unit>, IRequireTeamMembership
{
    public required Guid ProjectId { get; init; }
    public required string Name { get; init; }
    public string? Description { get; init; }
}
