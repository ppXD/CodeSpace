using CodeSpace.Messages.Authorization;
using CodeSpace.Messages.Dtos.Repositories;
using CodeSpace.Messages.Mediation;

namespace CodeSpace.Messages.Queries.Repositories;

public sealed record GetRepositoryQuery : IQuery<RepositoryDetail?>, IRequireRepositoryAccess
{
    public required Guid RepositoryId { get; init; }
}
