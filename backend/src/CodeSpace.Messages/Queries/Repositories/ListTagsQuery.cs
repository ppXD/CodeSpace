using CodeSpace.Messages.Authorization;
using CodeSpace.Messages.Dtos.Providers;
using CodeSpace.Messages.Mediation;

namespace CodeSpace.Messages.Queries.Repositories;

/// <summary>Live-fetch git tags (newest-first) for the Releases page's Tags tab. Membership enforced via <see cref="IRequireRepositoryAccess"/>.</summary>
public sealed record ListTagsQuery : IQuery<IReadOnlyList<RemoteTag>>, IRequireRepositoryAccess
{
    public const int DefaultPerPage = 30;
    public const int MaxPerPage = 100;

    public required Guid RepositoryId { get; init; }
    public int Page { get; init; } = 1;
    public int PerPage { get; init; } = DefaultPerPage;
}
