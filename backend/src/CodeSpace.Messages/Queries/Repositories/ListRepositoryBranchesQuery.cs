using CodeSpace.Messages.Authorization;
using CodeSpace.Messages.Dtos.Providers;
using CodeSpace.Messages.Mediation;

namespace CodeSpace.Messages.Queries.Repositories;

/// <summary>
/// Live-fetch all branches for a bound repository — the Code tab's branch picker. The repo's credential
/// makes the provider call; membership is enforced via <see cref="IRequireRepositoryAccess"/>.
/// </summary>
public sealed record ListRepositoryBranchesQuery : IQuery<IReadOnlyList<RemoteBranch>>, IRequireRepositoryAccess
{
    public required Guid RepositoryId { get; init; }
}
