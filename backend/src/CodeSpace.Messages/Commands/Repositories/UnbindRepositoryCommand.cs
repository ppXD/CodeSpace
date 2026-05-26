using CodeSpace.Messages.Authorization;
using CodeSpace.Messages.Mediation;
using MediatR;

namespace CodeSpace.Messages.Commands.Repositories;

public sealed record UnbindRepositoryCommand : ICommand<Unit>, IRequireRepositoryAccess
{
    public required Guid RepositoryId { get; init; }
}
