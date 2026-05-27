using CodeSpace.Messages.Authorization;
using MediatR;

namespace CodeSpace.Messages.Commands.Projects;

/// <summary>
/// Soft-delete a project. Service refuses when the project still has active repositories
/// — operator must move/unbind repos first. Variables inside the project are soft-deleted
/// as part of the same SaveChanges (cascade).
/// <para>The "default" slug cannot be deleted — every team must have at least one project.</para>
/// </summary>
public sealed record DeleteProjectCommand : IRequest<MediatR.Unit>, IRequireTeamMembership
{
    public required Guid ProjectId { get; init; }
}
