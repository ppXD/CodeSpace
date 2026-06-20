using CodeSpace.Messages.Authorization;
using CodeSpace.Messages.Dtos.Providers;
using CodeSpace.Messages.Mediation;

namespace CodeSpace.Messages.Queries.Repositories;

/// <summary>
/// Single release by tag for the in-app release-detail page. Membership enforced via
/// <see cref="IRequireRepositoryAccess"/>. The tag travels as a query value so any tag string
/// (slashes, dots, spaces) round-trips without path-segment escaping headaches.
/// </summary>
public sealed record GetReleaseQuery : IQuery<RemoteRelease>, IRequireRepositoryAccess
{
    public required Guid RepositoryId { get; init; }
    public required string Tag { get; init; }
}
