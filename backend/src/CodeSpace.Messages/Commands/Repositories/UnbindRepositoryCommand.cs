using CodeSpace.Messages.Authorization;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Mediation;
using MediatR;

namespace CodeSpace.Messages.Commands.Repositories;

/// <summary>
/// Remove a repository from a project, or from the team. <see cref="TeamPermissions.ReposManage"/> — the tier that
/// binds one — as well as the repository marker, which on its own proves only membership.
/// </summary>
public sealed record UnbindRepositoryCommand : ICommand<Unit>, IRequireRepositoryAccess, IRequireTeamPermission
{
    public string RequiredPermission => TeamPermissions.ReposManage;

    public required Guid RepositoryId { get; init; }

    /// <summary>Remove only this project's link (N:M) — the repo survives while other projects use it.
    /// Null removes the repository from the team entirely.</summary>
    public Guid? ProjectId { get; init; }
}
