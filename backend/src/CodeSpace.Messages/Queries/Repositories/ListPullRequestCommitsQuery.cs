using CodeSpace.Messages.Authorization;
using CodeSpace.Messages.Dtos.Providers;
using CodeSpace.Messages.Mediation;

namespace CodeSpace.Messages.Queries.Repositories;

public sealed record ListPullRequestCommitsQuery : IQuery<IReadOnlyList<RemotePullRequestCommit>>, IRequireRepositoryAccess
{
    public required Guid RepositoryId { get; init; }
    public required int Number { get; init; }
}
