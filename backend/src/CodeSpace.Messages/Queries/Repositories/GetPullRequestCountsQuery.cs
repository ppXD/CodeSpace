using CodeSpace.Messages.Authorization;
using CodeSpace.Messages.Dtos.Providers;
using CodeSpace.Messages.Mediation;

namespace CodeSpace.Messages.Queries.Repositories;

/// <summary>
/// Total open + closed PR counts for a repository. Cheap one-call aggregation
/// (GitHub Search; GitLab GraphQL); cached aggressively on the SPA side so the
/// "Open N · Closed M" tab chips don't refetch on every page navigation.
/// </summary>
public sealed record GetPullRequestCountsQuery : IQuery<RemotePullRequestCounts>, IRequireRepositoryAccess
{
    public required Guid RepositoryId { get; init; }
}
