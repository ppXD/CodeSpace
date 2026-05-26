using CodeSpace.Messages.Authorization;
using MediatR;

namespace CodeSpace.Messages.Commands.Projects;

/// <summary>
/// Update display name + description of an existing Project. Slug is immutable (changing it
/// would invalidate workflow variable refs <c>project.{slug}.X</c> across the team).
/// </summary>
public sealed record UpdateProjectCommand : IRequest<Unit>, IRequireTeamMembership
{
    public required Guid ProjectId { get; init; }
    public required string Name { get; init; }
    public string? Description { get; init; }
}
