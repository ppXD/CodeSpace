using CodeSpace.Messages.Authorization;
using CodeSpace.Messages.Dtos.Providers;
using CodeSpace.Messages.Enums;
using CodeSpace.Messages.Mediation;

namespace CodeSpace.Messages.Queries.Repositories;

/// <summary>
/// Live-fetch pull/merge requests for a bound repository. The repo's credential is used
/// to call the provider — credentials must be Active and the repo must be Active too;
/// the handler short-circuits with a typed error otherwise. Membership is enforced via
/// <see cref="IRequireRepositoryAccess"/>.
/// </summary>
public sealed record ListPullRequestsQuery : IQuery<IReadOnlyList<RemotePullRequest>>, IRequireRepositoryAccess
{
    /// <summary>Default page size — matches GitHub's own list pagination default and keeps the initial request snappy even on repos with thousands of closed PRs.</summary>
    public const int DefaultPerPage = 30;

    /// <summary>Upper bound for safety — both providers cap at 100 server-side, so anything higher silently degrades.</summary>
    public const int MaxPerPage = 100;

    public required Guid RepositoryId { get; init; }

    /// <summary>Null returns all states. Open/Draft → still-open on provider; Merged/Closed → finished.</summary>
    public PullRequestState? State { get; init; }

    /// <summary>1-based page index. Defaults to 1; the handler clamps anything lower.</summary>
    public int Page { get; init; } = 1;

    /// <summary>Page size. Defaults to <see cref="DefaultPerPage"/>; clamped to [1, <see cref="MaxPerPage"/>].</summary>
    public int PerPage { get; init; } = DefaultPerPage;
}
