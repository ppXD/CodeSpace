using CodeSpace.Messages.Authorization;
using CodeSpace.Messages.Mediation;
using MediatR;

namespace CodeSpace.Messages.Commands.Repositories;

public sealed record UnbindRepositoryCommand : ICommand<Unit>, IRequireRepositoryAccess
{
    public required Guid RepositoryId { get; init; }

    /// <summary>Remove only this project's link (N:M) — the repo survives while other projects use it.
    /// Null removes the repository from the team entirely.</summary>
    public Guid? ProjectId { get; init; }
}
