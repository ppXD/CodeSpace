using CodeSpace.Messages.Authorization;
using CodeSpace.Messages.Mediation;

namespace CodeSpace.Messages.Commands.Projects;

/// <summary>
/// Re-parent an active repository to a different project in the same team.
/// Bound to the project-detail Repositories tab's row-hover action so operators
/// can re-organise their repos without unbinding + rebinding through the OAuth
/// dance. Idempotent — no-op when the repo is already in the target project.
/// </summary>
public sealed record MoveRepositoryToProjectCommand : ICommand<MediatR.Unit>, IRequireTeamMembership
{
    public required Guid RepositoryId { get; init; }
    public required Guid TargetProjectId { get; init; }
}
