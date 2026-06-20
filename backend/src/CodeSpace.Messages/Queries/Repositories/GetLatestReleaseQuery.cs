using CodeSpace.Messages.Authorization;
using CodeSpace.Messages.Dtos.Providers;
using CodeSpace.Messages.Mediation;

namespace CodeSpace.Messages.Queries.Repositories;

/// <summary>
/// Live fetch of the repository's latest release for the Code tab's Releases card. Null when the repo
/// has no releases. Best-effort, same source-read scope as the stats query. Membership enforced via
/// <see cref="IRequireRepositoryAccess"/>.
/// </summary>
public sealed record GetLatestReleaseQuery : IQuery<RemoteRelease?>, IRequireRepositoryAccess
{
    public required Guid RepositoryId { get; init; }
}
