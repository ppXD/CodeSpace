using CodeSpace.Messages.Authorization;
using CodeSpace.Messages.Dtos.Providers;
using CodeSpace.Messages.Enums;
using CodeSpace.Messages.Mediation;

namespace CodeSpace.Messages.Queries.Repositories;

/// <summary>
/// Live-fetch issues for a bound repository — the read half of the Issues tab, mirroring
/// <see cref="ListPullRequestsQuery"/>. The repo's credential calls the provider; credentials
/// must be Active and the repo Active too. Membership is enforced via
/// <see cref="IRequireRepositoryAccess"/>.
/// </summary>
public sealed record ListIssuesQuery : IQuery<IReadOnlyList<RemoteIssue>>, IRequireRepositoryAccess
{
    /// <summary>Default page size — matches the PR list default so both tabs page identically.</summary>
    public const int DefaultPerPage = 30;

    /// <summary>Upper bound for safety — both providers cap at 100 server-side.</summary>
    public const int MaxPerPage = 100;

    public required Guid RepositoryId { get; init; }

    /// <summary>Null returns all states. Open → still-open on provider; Closed → closed.</summary>
    public IssueState? State { get; init; }

    /// <summary>1-based page index. Defaults to 1; the handler clamps anything lower.</summary>
    public int Page { get; init; } = 1;

    /// <summary>Page size. Defaults to <see cref="DefaultPerPage"/>; clamped to [1, <see cref="MaxPerPage"/>].</summary>
    public int PerPage { get; init; } = DefaultPerPage;
}
