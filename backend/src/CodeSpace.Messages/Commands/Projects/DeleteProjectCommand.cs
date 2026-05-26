using CodeSpace.Messages.Authorization;
using MediatR;

namespace CodeSpace.Messages.Commands.Projects;

/// <summary>
/// Soft-delete a Project + cascade-soft-delete its variables. Workflows referencing this
/// project's variables will fail save-time validation on next edit — intentional, so the
/// operator sees a clear "project X is gone" signal instead of silent run-time errors.
/// </summary>
public sealed record DeleteProjectCommand : IRequest<Unit>, IRequireTeamMembership
{
    public required Guid ProjectId { get; init; }
}
