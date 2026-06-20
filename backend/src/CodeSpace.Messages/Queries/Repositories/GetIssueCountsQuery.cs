using CodeSpace.Messages.Authorization;
using CodeSpace.Messages.Dtos.Providers;
using CodeSpace.Messages.Mediation;

namespace CodeSpace.Messages.Queries.Repositories;

/// <summary>
/// Total open + closed issue counts for a repository. Cheap one-call aggregation
/// (GitHub Search; GitLab GraphQL); cached on the SPA so the "Open N · Closed M"
/// filter chips don't refetch on every page navigation. Mirrors
/// <see cref="GetPullRequestCountsQuery"/>.
/// </summary>
public sealed record GetIssueCountsQuery : IQuery<RemoteIssueCounts>, IRequireRepositoryAccess
{
    public required Guid RepositoryId { get; init; }
}
