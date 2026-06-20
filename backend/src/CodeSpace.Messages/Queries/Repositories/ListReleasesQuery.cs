using CodeSpace.Messages.Authorization;
using CodeSpace.Messages.Dtos.Providers;
using CodeSpace.Messages.Mediation;

namespace CodeSpace.Messages.Queries.Repositories;

/// <summary>
/// Live-fetch releases (newest-first, with notes + assets) for the Releases page. Best-effort source-read,
/// paginated like the issue/PR lists. Membership enforced via <see cref="IRequireRepositoryAccess"/>.
/// </summary>
public sealed record ListReleasesQuery : IQuery<IReadOnlyList<RemoteRelease>>, IRequireRepositoryAccess
{
    public const int DefaultPerPage = 30;
    public const int MaxPerPage = 100;

    public required Guid RepositoryId { get; init; }
    public int Page { get; init; } = 1;
    public int PerPage { get; init; } = DefaultPerPage;
}
